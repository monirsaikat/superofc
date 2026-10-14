using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperOfc.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Devices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Device",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Ip = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    CommKeyProtected = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    MachineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SerialNumber = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Firmware = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LocationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSyncLocal = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    StatusMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ClockOffsetSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    DeletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Device", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceSyncLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Pulled = table.Column<int>(type: "INTEGER", nullable: false),
                    Inserted = table.Column<int>(type: "INTEGER", nullable: false),
                    Duplicates = table.Column<int>(type: "INTEGER", nullable: false),
                    Unmapped = table.Column<int>(type: "INTEGER", nullable: false),
                    Warning = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceSyncLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FingerprintBackup",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FingerIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Flag = table.Column<int>(type: "INTEGER", nullable: false),
                    DataProtected = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    SourceDeviceSerial = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CapturedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FingerprintBackup", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FingerprintBackup_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UnmappedPunch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeviceUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    LocalTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PunchAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DedupKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    VerifyMode = table.Column<int>(type: "INTEGER", nullable: false),
                    InOutMode = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnmappedPunch", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceUserMap",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NameOnDevice = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceUserMap", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceUserMap_Device_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Device",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceUserMap_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Device_CompanyId",
                table: "Device",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Device_CompanyId_Name",
                table: "Device",
                columns: new[] { "CompanyId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSyncLog_CompanyId",
                table: "DeviceSyncLog",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSyncLog_DeviceId_StartedAtUtc",
                table: "DeviceSyncLog",
                columns: new[] { "DeviceId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceUserMap_CompanyId",
                table: "DeviceUserMap",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceUserMap_DeviceId_DeviceUserId",
                table: "DeviceUserMap",
                columns: new[] { "DeviceId", "DeviceUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceUserMap_EmployeeId",
                table: "DeviceUserMap",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_FingerprintBackup_CompanyId",
                table: "FingerprintBackup",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_FingerprintBackup_EmployeeId_FingerIndex",
                table: "FingerprintBackup",
                columns: new[] { "EmployeeId", "FingerIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UnmappedPunch_CompanyId",
                table: "UnmappedPunch",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_UnmappedPunch_DedupKey",
                table: "UnmappedPunch",
                column: "DedupKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UnmappedPunch_DeviceId_DeviceUserId",
                table: "UnmappedPunch",
                columns: new[] { "DeviceId", "DeviceUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceSyncLog");

            migrationBuilder.DropTable(
                name: "DeviceUserMap");

            migrationBuilder.DropTable(
                name: "FingerprintBackup");

            migrationBuilder.DropTable(
                name: "UnmappedPunch");

            migrationBuilder.DropTable(
                name: "Device");
        }
    }
}
