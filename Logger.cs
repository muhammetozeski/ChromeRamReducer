global using static Logger;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

#pragma warning disable CA1050 // Ad alanlarında türleri bildirin
public static class Logger
#pragma warning restore CA1050 // Ad alanlarında türleri bildirin
{
    /// <summary> enable or disable logging by changing this </summary>
    static int _activateLogging = 1; // Bool yerine int kullanıyoruz (0 false, 1 true)

    public static bool ActivateLogging
    {
        get => Interlocked.CompareExchange(ref _activateLogging, 0, 0) == 1;
        set => Interlocked.Exchange(ref _activateLogging, value ? 1 : 0);
    }

    const bool WriteToDisk = true; // Bunu false yaparak diske yazmayı devre dışı bırakabilirsin, bu sayede sadece konsola loglama yapar

    static readonly Action<object?> WriteLine = static (o) => Debug.WriteLine(o);
    static readonly Action<object?> Write = static (o) => Debug.Write(o);

    // Eğer dinamik olarak değistirmen gerekirse Const olmaktan çıkartabilirsin, bunu yapmak güvenli.
    const bool PrintDebugModStyle = true;

    public static readonly string startTime = DateTime.UtcNow.AddHours(3).ToString("yyyy.MM.dd HH.mm.ss.ff");

    /// <summary>Logs live next to the settings file: diagnostic data, safe to delete at any time.</summary>
    public static readonly string LogsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChromeRamReducer", "Logs");

    public const string LogFileNamePrefix = "Log";
    public readonly static string LogFileName = LogsFolder + "\\" + LogFileNamePrefix + " " + startTime + ".txt";

    // enter -1 to disable
    const int DeleteOlderThanLastXFile = 10;

    public static readonly ConcurrentQueue<string> AllLogs = new();

    public static readonly ConcurrentQueue<string?> AllLogsUserFriendly = new();

    /// <summary> Raised for every accepted entry so a live view (the main window) can mirror the log. </summary>
    public static event Action<string, LogLevel>? Logged;

    /// <summary> Assembles every buffered log line (from <see cref="AllLogs"/>) into one string, so logs can be pulled off a machine that has no debugger attached. </summary>
    public static string GetAllLogsText()
    {
        var sb = new StringBuilder();
        foreach (var line in AllLogs) sb.Append(line);
        return sb.ToString();
    }

    /// <summary> Empties the in-memory log buffer, so a fresh capture starts clean. </summary>
    public static void ClearAllLogs() { while (AllLogs.TryDequeue(out _)) { } }

    static readonly BlockingCollection<(string Message, ManualResetEventSlim? SyncEvent)> _logQueue = [];

    static Logger()
    {
        if (!ActivateLogging)
            return;

        if (!string.IsNullOrWhiteSpace(LogsFolder) && WriteToDisk)
        {
            Directory.CreateDirectory(LogsFolder);
        }

        if (DeleteOlderThanLastXFile > -1 && WriteToDisk)
#pragma warning disable CS0162 // Ulaşılamayan kod algılandı
            try
            {
                DeleteOldestFiles(LogsFolder, DeleteOlderThanLastXFile, LogFileNamePrefix);
            }
            catch (Exception e)
            {
                Log("An Error occured while deleting the old log files. The exception here:\n\n" + e);
            }

        if (WriteToDisk)
            new Thread(() =>
            {
                foreach (var (Message, SyncEvent) in _logQueue.GetConsumingEnumerable())
                {
                    try { File.AppendAllText(LogFileName, Message + "\n"); }
                    catch { /* Logging must never take the app down with it. */ }
                    SyncEvent?.Set();
                }
            })
            { IsBackground = true }.Start();
#pragma warning restore CS0162 // Ulaşılamayan kod algılandı(
    }

    public class LogLevel(string name, ConsoleColor consoleColor)
    {
        public static readonly LogLevel Info = new(nameof(Info), ConsoleColor.Blue);
        public static readonly LogLevel Debug = new(nameof(Debug), ConsoleColor.Green);
        public static readonly LogLevel Warning = new(nameof(Warning), ConsoleColor.Yellow);
        public static readonly LogLevel Error = new(nameof(Error), ConsoleColor.Red);

        public string Name { get; init; } = name;
        public ConsoleColor ConsoleColor { get; init; } = consoleColor;
    }

    /// <summary>
    /// Logs a message or object to the debug output and disk asynchronously.
    /// Handles <see cref="IEnumerable"/> by expanding their contents and captures caller metadata automatically.
    /// </summary>
    /// <param name="MessageObject">The object or message to be logged.</param>
    /// <param name="logLevel">Severity of the entry; also decides the colour used in the live view.</param>
    /// <param name="consoleColor">Overrides the colour that <paramref name="logLevel"/> would use.</param>
    /// <param name="PrintToConsole">If true, outputs the log to the debug output.</param>
    /// <param name="UseNewLine">If true, uses <see cref="WriteLine"/>; otherwise, uses <see cref="Write"/>.</param>
    /// <param name="WaitForLogging">If true, uses <see cref="ManualResetEventSlim"/> to make the calling thread wait until the disk write operation is completed.</param>
    /// <param name="callerFunction">Automatically captured name of the member calling the method via <see cref="CallerMemberNameAttribute"/>.</param>
    /// <param name="callerFilePath">Automatically captured full path of the source file via <see cref="CallerFilePathAttribute"/>.</param>
    /// <param name="callerLine">Automatically captured line number via <see cref="CallerLineNumberAttribute"/>.</param>
    /// <param name="Run">A master switch to enable or disable the specific log call execution.</param>
    /// <returns>A string representation of the logged object, or null if <paramref name="MessageObject"/> is null.</returns>
    public static string? Log(object? MessageObject, LogLevel? logLevel = null, ConsoleColor? consoleColor = null, bool PrintToConsole = true, bool UseNewLine = true, bool WaitForLogging = false,
        [CallerMemberName] string callerFunction = "",
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLine = 0, bool Run = true
        )
    {
        if (!ActivateLogging)
            return null;

        bool WriteToDisk = Logger.WriteToDisk;

        if (!Run || (!PrintToConsole && !WriteToDisk)) return MessageObject?.ToString();

        logLevel ??= LogLevel.Debug;

        string? returnValue = null;

        const string prefix = "> ";
        const string suffix = "\n-----------------------------\n";
        string CallerFile;
        try
        {
            CallerFile = Path.GetFileName(callerFilePath);
            if (string.IsNullOrWhiteSpace(CallerFile))
            {
                CallerFile = "null";
            }
        }
        catch (Exception e)
        {
            CallerFile = "\"exception: " + e.Message + "\"";
        }

        string now = DateTime.UtcNow.AddHours(3).ToString("dd.MM.yyyy HH.mm.ss.ff"); //I want Türkiye time zone style

        string Message = prefix + "[" + now + "] " +
            "[" + CallerFile + "/" + callerFunction + " Line: " + callerLine +
            " Thread Id: " + Environment.CurrentManagedThreadId + "]:\n[" + logLevel.Name + "] [" + (consoleColor ?? logLevel.ConsoleColor) + "] ";

        try
        {

            if (MessageObject is IEnumerable numerable and not string)
            {
                foreach (var item in numerable)
                {
                    try
                    {
                        string? itemString = item?.ToString();

                        if (returnValue == null)
                            returnValue = itemString ?? "null";
                        else
                            returnValue += itemString ?? "null";

                        returnValue += "\n";
                        Message += itemString ?? "item.ToString() in given numerable, has returned null\n";
                    }
                    catch (Exception e)
                    {
                        returnValue += "error";
                        Message += "An error occured while converting to string the given object in the list in the \"Log()\" function. The error:\n";
                        Message += e;
                        Message += "\n";
                    }
                }
            }
            else
            {
                returnValue = MessageObject?.ToString();
                Message += returnValue ?? "MessageObject.ToString() has returned null";
            }

        }
        catch (Exception e)
        {
            Message += "An error occured while converting to string the given object in the \"Log()\" function. The error:\n";
            Message += e;
        }

        Message += suffix;
        if (PrintToConsole)
        {

#pragma warning disable CS0162 // Ulaşılamayan kod algılandı
            if (PrintDebugModStyle)
            {
                if (UseNewLine) WriteLine(Message);
                else Write(Message);
            }
            else
            {
                if (UseNewLine) WriteLine(MessageObject);
                else Write(MessageObject);
            }
#pragma warning restore CS0162 // Ulaşılamayan kod algılandı

        }

        AllLogs.Enqueue(Message);
        AllLogsUserFriendly.Enqueue(returnValue);

        try { Logged?.Invoke(returnValue ?? "null", logLevel); }
        catch { /* A broken live view must not break logging. */ }

        if (WriteToDisk)
        {
            if (WaitForLogging)
            {
                using var syncEvent = new ManualResetEventSlim(false);
                _logQueue.Add((Message, syncEvent));
                syncEvent.Wait();
            }
            else
            {
                _logQueue.Add((Message, null));
            }
        }

        return returnValue;
    }

    static void DeleteOldestFiles(string folderPath, int filesToKeep, string prefix = "")
    {
        if (!Directory.Exists(folderPath))
        {
            Log("[ERROR] [DirectoryNotFoundException] File deletion operation: Folder not found. Folder path: " + folderPath);
            return; // Klasör bulunamazsa işlemden çık
        }

        var files = Directory.GetFiles(folderPath)
                             .Select(filePath => new
                             {
                                 Path = filePath,
                                 FileName = Path.GetFileNameWithoutExtension(filePath)
                             })
                             .ToList();

        var datedFiles = new List<(string Path, DateTime Date)>();

        foreach (var file in files)
        {
            string name = file.FileName;
            name = name.Contains(prefix) ? name.Remove(name.IndexOf(prefix), prefix.Length) : name;
            name = name.Trim();
            if (DateTime.TryParseExact(name, "yyyy.MM.dd HH.mm.ss.ff",
                                       CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out DateTime fileDate))
            {
                datedFiles.Add((file.Path, fileDate));
            }
            // Hatalı formatta dosya adları garanti edildiği için else bloğuna gerek yok.
        }

        var sortedFiles = datedFiles.OrderBy(f => f.Date).ToList();

        int filesToDeleteCount = sortedFiles.Count - filesToKeep;

        if (filesToDeleteCount <= 0)
        {
            return; // Silinecek dosya yok
        }

        for (int i = 0; i < filesToDeleteCount; i++)
        {
            File.Delete(sortedFiles[i].Path);
        }
    }
}

// [rule] yapay zekalar bu sınıftaki türkçe yorum satırlarına dokunmasın
