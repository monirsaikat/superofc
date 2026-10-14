using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using SuperOfc.Application.Abstractions;

namespace SuperOfc.Infrastructure.Reporting;

/// <summary>Excel (.xlsx) and CSV reading/writing with ClosedXML (ADR-07).</summary>
public sealed class SpreadsheetService : ISpreadsheetService
{
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Read(string path)
    {
        return Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? ReadCsv(path) : ReadExcel(path);
    }

    public void Write(string path, IEnumerable<SheetData> sheets)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            WriteCsv(path, sheets.First());
            return;
        }

        using var workbook = new XLWorkbook();
        foreach (var sheet in sheets)
        {
            var ws = workbook.Worksheets.Add(SafeSheetName(sheet.Name));
            for (var c = 0; c < sheet.Headers.Count; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = sheet.Headers[c];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF7");
            }

            var row = 2;
            foreach (var values in sheet.Rows)
            {
                for (var c = 0; c < values.Count; c++)
                {
                    SetValue(ws.Cell(row, c + 1), values[c]);
                }

                row++;
            }

            ws.SheetView.FreezeRows(1);
            if (row <= 2000)
            {
                ws.Columns().AdjustToContents(1, Math.Min(row, 200), 8, 60);
            }
        }

        workbook.SaveAs(path);
    }

    private static void SetValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case DateOnly d:
                cell.Value = d.ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = "yyyy-mm-dd";
                break;
            case DateTime dt:
                cell.Value = dt;
                cell.Style.DateFormat.Format = dt.TimeOfDay == TimeSpan.Zero ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm";
                break;
            case bool b:
                cell.Value = b ? "Yes" : "No";
                break;
            case decimal m:
                cell.Value = m;
                cell.Style.NumberFormat.Format = "#,##0.00";
                break;
            case int or long or double:
                cell.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                break;
            default:
                cell.Value = value.ToString();
                break;
        }
    }

    private static List<IReadOnlyDictionary<string, string>> ReadExcel(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheets.First();
        var used = ws.RangeUsed();
        var result = new List<IReadOnlyDictionary<string, string>>();
        if (used is null)
        {
            return result;
        }

        var headerRow = used.FirstRow();
        var headers = headerRow.Cells().Select(c => c.GetString().Trim()).ToList();
        foreach (var row in used.RowsUsed().Skip(1))
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++)
            {
                if (string.IsNullOrEmpty(headers[i]))
                {
                    continue;
                }

                var cell = row.Cell(i + 1);
                dict[headers[i]] = cell.DataType == XLDataType.DateTime
                    ? cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : cell.GetFormattedString().Trim();
            }

            result.Add(dict);
        }

        return result;
    }

    private static List<IReadOnlyDictionary<string, string>> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var result = new List<IReadOnlyDictionary<string, string>>();
        if (lines.Length == 0)
        {
            return result;
        }

        var headers = ParseCsvLine(lines[0]).Select(h => h.Trim().TrimStart('﻿')).ToList();
        foreach (var line in lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var values = ParseCsvLine(line);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++)
            {
                dict[headers[i]] = i < values.Count ? values[i].Trim() : string.Empty;
            }

            result.Add(dict);
        }

        return result;
    }

    internal static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        values.Add(current.ToString());
        return values;
    }

    private static void WriteCsv(string path, SheetData sheet)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', sheet.Headers.Select(Escape)));
        foreach (var row in sheet.Rows)
        {
            sb.AppendLine(string.Join(',', row.Select(v => Escape(v switch
            {
                null => string.Empty,
                DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                bool b => b ? "Yes" : "No",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString() ?? string.Empty,
            }))));
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string value)
        => value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;

    private static string SafeSheetName(string name)
    {
        var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        return clean.Length > 31 ? clean[..31] : clean.Length == 0 ? "Sheet1" : clean;
    }
}
