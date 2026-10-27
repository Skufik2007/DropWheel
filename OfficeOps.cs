using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DropWheel;

// Documents, spreadsheets and presentations through Microsoft Office driven
// invisibly over COM, or LibreOffice when Office isn't installed.
internal static class OfficeOps
{
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    static (string progId, string process) OfficeAppFor(FileKind kind) => kind switch
    {
        FileKind.Document => ("Word.Application", "WINWORD"),
        FileKind.Spreadsheet => ("Excel.Application", "EXCEL"),
        _ => ("PowerPoint.Application", "POWERPNT"),
    };

    static string? LibreOfficePath()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            string path = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    public static async Task ConvertAsync(FileKind kind, string file, string target, string output, CancellationToken ct)
    {
        var (progId, process) = OfficeAppFor(kind);
        if (Type.GetTypeFromProgID(progId) != null)
        {
            await RunOfficeAsync(kind, progId, process, file, target, output, ct);
            return;
        }
        if (LibreOfficePath() is string soffice)
        {
            await RunLibreOfficeAsync(soffice, file, target, output, ct);
            return;
        }
        throw new InvalidOperationException("для документов нужен Microsoft Office или LibreOffice");
    }

    // ------------------------------------------------------------- Office

    static HashSet<int> ProcessIds(string name) =>
        Process.GetProcessesByName(name).Select(p => p.Id).ToHashSet();

    static async Task RunOfficeAsync(FileKind kind, string progId, string processName, string file, string target,
        string output, CancellationToken ct)
    {
        var before = ProcessIds(processName);
        var ownPid = new StrongBox<int>(0);

        Task work = StaRunner.Run(() =>
        {
            dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID(progId)!)!;
            // Our own instance only if a new process appeared: PowerPoint
            // joins one the user already has open, and that one must not be
            // closed.
            var started = ProcessIds(processName).Except(before).ToList();
            ownPid.Value = started.Count == 1 ? started[0] : 0;
            try
            {
                switch (kind)
                {
                    case FileKind.Document:
                        ConvertWithWord(app, file, target, output);
                        break;
                    case FileKind.Spreadsheet:
                        ConvertWithExcel(app, file, target, output);
                        break;
                    default:
                        ConvertWithPowerPoint(app, file, target, output);
                        break;
                }
            }
            finally
            {
                if (ownPid.Value != 0)
                {
                    try
                    {
                        app.Quit();
                    }
                    catch
                    {
                    }
                }
                Marshal.FinalReleaseComObject((object)app);
                // Proxies the dynamic calls created along the way.
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        });

        Task finished = await Task.WhenAny(work, Task.Delay(Timeout, ct));
        if (finished != work)
        {
            // A dialog nobody can see, or Office hanging: end our instance.
            if (ownPid.Value != 0)
            {
                try
                {
                    Process.GetProcessById(ownPid.Value).Kill();
                }
                catch
                {
                }
            }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("Office не ответил за 3 минуты");
        }

        try
        {
            await work;
        }
        finally
        {
            await EnsureExitedAsync(ownPid.Value);
        }
    }

    // Quit can be ignored while Office is still finishing an export, which
    // leaves an invisible instance behind. Ours holds no user documents, so
    // it is ended if it doesn't leave on its own.
    static async Task EnsureExitedAsync(int pid)
    {
        if (pid == 0)
        {
            return;
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(grace.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Write($"Office instance {pid} didn't quit, ending it");
                process.Kill();
            }
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (Exception ex)
        {
            Log.Write("Ending Office failed: " + ex.Message);
        }
    }

    // Plain text files have no encoding marker; UTF-8 if the bytes are valid
    // UTF-8, the system ANSI code page (1251 for Russian) otherwise.
    static int TextCodePage(string file)
    {
        byte[] bytes;
        using (var stream = File.OpenRead(file))
        {
            bytes = new byte[Math.Min(stream.Length, 4 << 20)];
            stream.ReadExactly(bytes);
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return 65001;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return 1200;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return 1201;
        try
        {
            new UTF8Encoding(false, true).GetString(bytes);
            return 65001;
        }
        catch (DecoderFallbackException)
        {
            return CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        }
    }

    static void ConvertWithWord(dynamic word, string file, string target, string output)
    {
        word.Visible = false;
        word.DisplayAlerts = 0; // wdAlertsNone
        TryDisableMacros(word);

        string ext = Catalog.ExtOf(file);
        dynamic documents = word.Documents;
        dynamic doc = ext is "txt" or "md"
            ? documents.Open(FileName: file, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                Visible: false, Encoding: TextCodePage(file))
            : documents.Open(FileName: file, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                Visible: false);
        try
        {
            switch (target)
            {
                case "pdf":
                    doc.ExportAsFixedFormat(OutputFileName: output, ExportFormat: 17);
                    break;
                case "txt":
                    doc.SaveAs2(FileName: output, FileFormat: 2, Encoding: 65001, AddToRecentFiles: false);
                    break;
                default:
                    int format = target switch
                    {
                        "docx" => 16, // wdFormatDocumentDefault
                        "doc" => 0, // wdFormatDocument97
                        "rtf" => 6,
                        "odt" => 23,
                        "html" => 10, // wdFormatFilteredHTML, without Word's markup
                        _ => throw new NotSupportedException(target),
                    };
                    doc.SaveAs2(FileName: output, FileFormat: format, AddToRecentFiles: false);
                    break;
            }
        }
        finally
        {
            doc.Close(SaveChanges: 0);
            Marshal.FinalReleaseComObject((object)doc);
            Marshal.FinalReleaseComObject((object)documents);
        }
    }

    static void ConvertWithExcel(dynamic excel, string file, string target, string output)
    {
        excel.Visible = false;
        excel.DisplayAlerts = false;
        excel.ScreenUpdating = false;
        TryDisableMacros(excel);

        dynamic workbooks = excel.Workbooks;
        dynamic workbook;
        string? textCopy = null;
        if (Catalog.ExtOf(file) == "csv")
        {
            // Open would guess the encoding and the separator; OpenText is
            // told both. Excel ignores what OpenText is told for a .csv file,
            // so it reads a .txt copy.
            int codePage = TextCodePage(file);
            string firstLine = File.ReadLines(file, Encoding.GetEncoding(codePage)).FirstOrDefault() ?? "";
            bool semicolon = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',');
            textCopy = Path.Combine(Path.GetTempPath(), $"DropWheel-{Guid.NewGuid():N}.txt");
            File.Copy(file, textCopy);
            workbooks.OpenText(Filename: textCopy, Origin: codePage, DataType: 1, TextQualifier: 1,
                Semicolon: semicolon, Comma: !semicolon, Tab: false);
            workbook = excel.ActiveWorkbook;
            dynamic sheet = workbook.Worksheets[1];
            sheet.Name = SheetName(Catalog.BaseNameOf(file));
            Marshal.FinalReleaseComObject((object)sheet);
        }
        else
        {
            workbook = workbooks.Open(Filename: file, UpdateLinks: 0, ReadOnly: true, AddToMru: false);
        }

        try
        {
            if (target == "pdf")
            {
                workbook.ExportAsFixedFormat(Type: 0, Filename: output);
            }
            else
            {
                int format = target switch
                {
                    "xlsx" => 51, // xlOpenXMLWorkbook
                    "xls" => 56, // xlExcel8
                    "csv" => 62, // xlCSVUTF8
                    "ods" => 60,
                    _ => throw new NotSupportedException(target),
                };
                workbook.SaveAs(Filename: output, FileFormat: format, Local: true);
            }
        }
        finally
        {
            workbook.Close(SaveChanges: false);
            Marshal.FinalReleaseComObject((object)workbook);
            Marshal.FinalReleaseComObject((object)workbooks);
            if (textCopy != null)
            {
                try
                {
                    File.Delete(textCopy);
                }
                catch
                {
                }
            }
        }
    }

    // Excel sheet names: up to 31 characters, none of []:*?/\
    static string SheetName(string name)
    {
        var cleaned = new string(name.Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim('\'');
        if (cleaned.Length == 0)
        {
            cleaned = "Лист1";
        }
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    static void ConvertWithPowerPoint(dynamic powerPoint, string file, string target, string output)
    {
        powerPoint.DisplayAlerts = 1; // ppAlertsNone
        TryDisableMacros(powerPoint);

        dynamic presentations = powerPoint.Presentations;
        // msoTrue is -1: read-only, no window.
        dynamic presentation = presentations.Open(FileName: file, ReadOnly: -1, Untitled: 0, WithWindow: 0);
        try
        {
            int format = target switch
            {
                "pptx" => 24, // ppSaveAsOpenXMLPresentation
                "ppt" => 1,
                "pdf" => 32,
                "odp" => 35,
                _ => throw new NotSupportedException(target),
            };
            presentation.SaveAs(FileName: output, FileFormat: format);
        }
        finally
        {
            presentation.Close();
            Marshal.FinalReleaseComObject((object)presentation);
            Marshal.FinalReleaseComObject((object)presentations);
        }
    }

    // Macros in the converted file never run.
    static void TryDisableMacros(dynamic app)
    {
        try
        {
            app.AutomationSecurity = 3; // msoAutomationSecurityForceDisable
        }
        catch
        {
        }
    }

    // -------------------------------------------------------- LibreOffice

    static async Task RunLibreOfficeAsync(string soffice, string file, string target, string output, CancellationToken ct)
    {
        string temp = Path.Combine(Path.GetTempPath(), "DropWheel-lo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string filter = target switch
            {
                "txt" => "txt:Text (encoded):UTF8",
                "csv" => "csv:Text - txt - csv (StarCalc):44,34,76",
                "html" => "html",
                _ => target,
            };
            var info = new ProcessStartInfo(soffice) { UseShellExecute = false, CreateNoWindow = true };
            // Its own profile, so a LibreOffice the user has open doesn't
            // swallow the request.
            info.ArgumentList.Add("-env:UserInstallation=" + new Uri(Path.Combine(temp, "profile")).AbsoluteUri);
            foreach (string arg in new[] { "--headless", "--norestore", "--nolockcheck", "--convert-to", filter, "--outdir", temp, file })
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                }
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("LibreOffice не ответил за 3 минуты");
            }

            string? produced = Directory.GetFiles(temp).FirstOrDefault();
            if (produced == null)
            {
                throw new InvalidOperationException("LibreOffice не смог сконвертировать файл");
            }
            File.Move(produced, output);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, true);
            }
            catch
            {
            }
        }
    }
}
