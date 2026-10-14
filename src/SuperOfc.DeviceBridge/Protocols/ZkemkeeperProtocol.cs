using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SuperOfc.DeviceBridge.Contracts;

namespace SuperOfc.DeviceBridge.Protocols;

/// <summary>
/// ZKTeco Standalone SDK (zkemkeeper.dll, COM) through late binding, so the bridge builds and runs without the SDK
/// installed. Calls follow docs/06 "SDK calls used". Must run on an STA thread (see <see cref="StaRunner"/>).
/// </summary>
internal sealed class ZkemkeeperProtocol : IDeviceProtocol
{
    private const string ProgId = "zkemkeeper.ZKEM.1";
    private static readonly Guid ClassId = new("00853A19-BD51-419B-9269-2DABE57EB61F");

    private readonly SdkActivation _activation;
    private dynamic? _zk;
    private int _machine = 1;
    private bool _connected;

    public ZkemkeeperProtocol(SdkActivation activation)
    {
        _activation = activation;
    }

    public string Name => "zkemkeeper";

    private dynamic Zk => _zk ?? throw new InvalidOperationException("Not connected.");

    public DeviceInfo Connect(DeviceAddress address)
    {
        _machine = address.MachineNumber;
        _zk = _activation.CreateInstance(ProgId, ClassId);
        Zk.SetCommPassword(address.CommKey);
        if (!(bool)Zk.Connect_Net(address.Ip, address.Port))
        {
            var error = LastError();

            // The SDK reports a wrong comm key as -2 or 4 depending on firmware; everything else is "unreachable".
            if (error is -2 or 4)
            {
                throw new DeviceException(BridgeErrors.WrongCommKey, "The device refused the communication key.");
            }

            throw new DeviceException(BridgeErrors.Unreachable, "Could not connect to " + address.Ip + ":" + address.Port + " (SDK error " + error + ").");
        }

        _connected = true;
        string serial = string.Empty, firmware = string.Empty, platform = string.Empty, name = string.Empty;
        Zk.GetSerialNumber(_machine, out serial);
        Zk.GetFirmwareVersion(_machine, ref firmware);
        TryCall(() => Zk.GetPlatform(_machine, ref platform));
        TryCall(() => Zk.GetDeviceStrInfo(_machine, 1, out name));
        int users = 0, logs = 0, fingers = 0;
        Zk.GetDeviceStatus(_machine, 2, ref users);
        Zk.GetDeviceStatus(_machine, 6, ref logs);
        Zk.GetDeviceStatus(_machine, 3, ref fingers);
        return new DeviceInfo
        {
            SerialNumber = serial ?? string.Empty,
            Firmware = firmware,
            Platform = platform,
            DeviceName = string.IsNullOrEmpty(name) ? null : name,
            UserCount = users,
            LogCount = logs,
            FingerCount = fingers,
            SupportsFace = platform?.IndexOf("face", StringComparison.OrdinalIgnoreCase) >= 0,
            DeviceTime = GetTime(),
        };
    }

    public DateTime GetTime()
    {
        int year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0;
        Check(Zk.GetDeviceTime(_machine, ref year, ref month, ref day, ref hour, ref minute, ref second), "read the clock");
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    public void SetTime(DateTime local)
    {
        Check(Zk.SetDeviceTime2(_machine, local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second), "set the clock");
        Zk.RefreshData(_machine);
    }

    public IEnumerable<DeviceLog> ReadLogs(DateTime? fromLocal, DateTime? toLocal, CancellationToken cancellationToken)
    {
        Zk.EnableDevice(_machine, false);
        try
        {
            if (!(bool)Zk.ReadGeneralLogData(_machine))
            {
                var error = LastError();
                if (error == 0)
                {
                    yield break; // no records
                }

                throw new DeviceException(BridgeErrors.Failed, "The device could not read its attendance logs (SDK error " + error + ").");
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string user = string.Empty;
                int verify = 0, inOut = 0, year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0, workCode = 0;
                if (!(bool)Zk.SSR_GetGeneralLogData(_machine, out user, out verify, out inOut, out year, out month, out day, out hour, out minute, out second, ref workCode))
                {
                    yield break;
                }

                var time = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
                if ((fromLocal is null || time >= fromLocal) && (toLocal is null || time <= toLocal))
                {
                    yield return new DeviceLog { DeviceUserId = user, TimestampLocal = time, VerifyMode = verify, InOutMode = inOut, WorkCode = workCode };
                }
            }
        }
        finally
        {
            Zk.EnableDevice(_machine, true);
        }
    }

    public List<DeviceUser> GetUsers()
    {
        var result = new List<DeviceUser>();
        Zk.EnableDevice(_machine, false);
        try
        {
            Zk.ReadAllUserID(_machine);
            while (true)
            {
                string id = string.Empty, name = string.Empty, password = string.Empty;
                int privilege = 0;
                bool enabled = false;
                if (!(bool)Zk.SSR_GetAllUserInfo(_machine, out id, out name, out password, out privilege, out enabled))
                {
                    break;
                }

                string card = string.Empty;
                TryCall(() => Zk.GetStrCardNumber(out card));
                result.Add(new DeviceUser { DeviceUserId = id, Name = (name ?? string.Empty).TrimEnd('\0'), Privilege = privilege, Enabled = enabled, CardNumber = string.IsNullOrEmpty(card) || card == "0" ? null : card });
            }
        }
        finally
        {
            Zk.EnableDevice(_machine, true);
        }

        return result;
    }

    public void SetUser(DeviceUser user)
    {
        Zk.EnableDevice(_machine, false);
        try
        {
            if (!string.IsNullOrEmpty(user.CardNumber))
            {
                Zk.SetStrCardNumber(user.CardNumber);
            }

            Check(Zk.SSR_SetUserInfo(_machine, user.DeviceUserId, user.Name, user.Password ?? string.Empty, user.Privilege, user.Enabled), "save the user");
            Zk.RefreshData(_machine);
        }
        finally
        {
            Zk.EnableDevice(_machine, true);
        }
    }

    public void DeleteUser(string deviceUserId)
    {
        // 12 = delete the user and all enrolment data.
        Check(Zk.SSR_DeleteEnrollData(_machine, deviceUserId, 12), "delete the user");
        Zk.RefreshData(_machine);
    }

    public List<FingerTemplate> GetTemplates(string deviceUserId)
    {
        var result = new List<FingerTemplate>();
        Zk.EnableDevice(_machine, false);
        try
        {
            for (var finger = 0; finger < 10; finger++)
            {
                string data = string.Empty;
                int length = 0, flag = 0;
                if ((bool)Zk.GetUserTmpExStr(_machine, deviceUserId, finger, out flag, out data, out length) && !string.IsNullOrEmpty(data))
                {
                    result.Add(new FingerTemplate { FingerIndex = finger, Data = data, Flag = flag });
                }
            }
        }
        finally
        {
            Zk.EnableDevice(_machine, true);
        }

        return result;
    }

    public void SetTemplates(string deviceUserId, IReadOnlyList<FingerTemplate> templates)
    {
        Zk.EnableDevice(_machine, false);
        try
        {
            foreach (var t in templates)
            {
                Check(Zk.SetUserTmpExStr(_machine, deviceUserId, t.FingerIndex, t.Flag, t.Data), "save a fingerprint");
            }

            Zk.RefreshData(_machine);
        }
        finally
        {
            Zk.EnableDevice(_machine, true);
        }
    }

    public void ClearLogs()
    {
        Check(Zk.ClearGLog(_machine), "clear the attendance logs");
        Zk.RefreshData(_machine);
    }

    public void Dispose()
    {
        if (_zk is not null)
        {
            try
            {
                if (_connected)
                {
                    _zk.Disconnect();
                }
            }
            catch (COMException)
            {
                // Already gone.
            }

            Marshal.FinalReleaseComObject(_zk);
            _zk = null;
        }
    }

    private int LastError()
    {
        var error = 0;
        TryCall(() => Zk.GetLastError(ref error));
        return error;
    }

    private void Check(object ok, string what)
    {
        if (ok is bool b && b)
        {
            return;
        }

        throw new DeviceException(BridgeErrors.Failed, "The device could not " + what + " (SDK error " + LastError() + ").");
    }

    private static void TryCall(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // Optional call not supported by this SDK or firmware.
        }
    }
}

/// <summary>
/// Creates the SDK COM object: through a side-by-side manifest next to the DLL when present (no registration needed),
/// otherwise from the registry (vendor setup).
/// </summary>
internal sealed class SdkActivation : IDisposable
{
    private readonly IntPtr _context = new(-1);
    private readonly string? _manifest;

    public SdkActivation(string folder)
    {
        var manifest = Path.Combine(folder, "zkemkeeper.sxs.manifest");
        if (File.Exists(manifest) && File.Exists(Path.Combine(folder, "zkemkeeper.dll")))
        {
            _manifest = manifest;
            var context = new ActCtx
            {
                cbSize = Marshal.SizeOf<ActCtx>(),
                lpSource = manifest,
                lpAssemblyDirectory = folder,
                dwFlags = 0x004, // ACTCTX_FLAG_ASSEMBLY_DIRECTORY_VALID
            };
            _context = CreateActCtx(ref context);
        }
    }

    public bool UsesManifest => _manifest is not null && _context != new IntPtr(-1);

    public bool IsAvailable => UsesManifest || Type.GetTypeFromProgID(ZkemkeeperProgId) is not null;

    private const string ZkemkeeperProgId = "zkemkeeper.ZKEM.1";

    public dynamic CreateInstance(string progId, Guid classId)
    {
        var cookie = IntPtr.Zero;
        var activated = UsesManifest && ActivateActCtx(_context, out cookie);
        try
        {
            var type = Type.GetTypeFromCLSID(classId, throwOnError: false) ?? Type.GetTypeFromProgID(progId);
            if (type is null)
            {
                throw new DeviceException(BridgeErrors.SdkMissing, "The ZKTeco SDK is not installed.");
            }

            try
            {
                return Activator.CreateInstance(type) ?? throw new DeviceException(BridgeErrors.SdkMissing, "The ZKTeco SDK could not be started.");
            }
            catch (COMException ex)
            {
                throw new DeviceException(BridgeErrors.SdkMissing, "The ZKTeco SDK could not be started: " + ex.Message, ex);
            }
        }
        finally
        {
            if (activated)
            {
                DeactivateActCtx(0, cookie);
            }
        }
    }

    public void Dispose()
    {
        if (UsesManifest)
        {
            ReleaseActCtx(_context);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ActCtx
    {
        public int cbSize;
        public uint dwFlags;
        public string lpSource;
        public ushort wProcessorArchitecture;
        public ushort wLangId;
        public string? lpAssemblyDirectory;
        public string? lpResourceName;
        public string? lpApplicationName;
        public IntPtr hModule;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateActCtxW")]
    private static extern IntPtr CreateActCtx(ref ActCtx actctx);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ActivateActCtx(IntPtr hActCtx, out IntPtr lpCookie);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeactivateActCtx(int dwFlags, IntPtr lpCookie);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ReleaseActCtx(IntPtr hActCtx);
}

internal sealed class ZkemkeeperProtocolFactory : IDeviceProtocolFactory, IDisposable
{
    private readonly SdkActivation _activation;

    public ZkemkeeperProtocolFactory(string sdkFolder)
    {
        _activation = new SdkActivation(sdkFolder);
    }

    public string Name => _activation.UsesManifest ? "zkemkeeper (side-by-side)" : "zkemkeeper (registered)";

    public bool IsAvailable => _activation.IsAvailable;

    public IDeviceProtocol Create() => new ZkemkeeperProtocol(_activation);

    public void Dispose() => _activation.Dispose();
}

/// <summary>Runs SDK work on a fresh STA thread (zkemkeeper is an apartment-threaded COM server).</summary>
internal static class StaRunner
{
    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            if (error is DeviceException)
            {
                throw error;
            }

            if (error is OperationCanceledException)
            {
                throw new OperationCanceledException(error.Message, error);
            }

            throw new DeviceException(BridgeErrors.Failed, error is Win32Exception or COMException ? "Device driver error: " + error.Message : error.Message, error);
        }

        return result;
    }
}
