using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsInput;
using WindowsInput.Native;

[DllImport("user32.dll")]
static extern IntPtr GetForegroundWindow();

[DllImport("user32.dll")]
static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

Config config = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Starfield", "Saves"));

if (File.Exists("quicksave.json"))
{
    Console.WriteLine("Loading quicksave.json... existing file found");
    config = JsonSerializer.Deserialize<Config>(File.ReadAllText("quicksave.json"))!;
}
else
{
    string path = Path.Combine(Directory.GetCurrentDirectory(), "quicksave.json");
    Console.WriteLine($"Loading quicksave.json... writing default settings to {path} because no existing file found");
    File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
}

if (!Directory.Exists(config.SaveDirectory))
{
    Console.WriteLine($"saveDirectory {config.SaveDirectory} does not exist");
    return;
}

Console.WriteLine("\nSavedirectory: " + config.SaveDirectory);
Console.WriteLine("UpdateInterval: " + config.UpdateInterval);
Console.WriteLine("QuicksaveSave: " + config.QuicksaveSave);
Console.WriteLine("QuicksaveSaveInterval: " + config.QuicksaveSaveInterval);
Console.WriteLine("QuicksaveCopy: " + config.QuicksaveCopy);
Console.WriteLine("QuicksaveCount: " + config.QuicksaveCount);
Console.WriteLine("VerboseLevel: " + config.VerboseLevel + "\n");


CancellationTokenSource cancel = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

DateTime? quicksaveLastCopy = null;
InputSimulator inputSimulator = new();

while (!cancel.Token.IsCancellationRequested)
{
    try
    {
        Task.Delay(TimeSpan.FromSeconds(config.UpdateInterval), cancel.Token).Wait();
    }
    catch (AggregateException ex)
    {
        if (ex.InnerException is TaskCanceledException)
        {
            break;
        }

        throw;
    }

    GetWindowThreadProcessId(GetForegroundWindow(), out uint processId);
    Process process = Process.GetProcessById((int)processId);

    bool linesWritten = false;
    if (process.MainWindowTitle != config.ProcessName)
    {
        if (config.VerboseLevel > 0)
        {
            Console.WriteLine($"Skipping this update because {config.ProcessName} was not in focus");
            linesWritten = true;
        }
        continue;
    }

    IEnumerable<string> saveFiles = Directory.EnumerateFiles(config.SaveDirectory);
    IEnumerable<(string, DateTime)> quicksaveFileCandidates = saveFiles
        .Where(x => Path.GetFileName(x).StartsWith("Quicksave0") && Path.GetFileName(x).EndsWith(".sfs") )
        .Select(x => (x, File.GetLastWriteTime(x)))
        .OrderByDescending(x => x.Item2);

    int quicksaveFileCandidatesCount = quicksaveFileCandidates.Count();
    if (quicksaveFileCandidatesCount == 0)
    {
        if (config.VerboseLevel > 0)
        {
            Console.WriteLine($"Skipping this update because no quicksaves were found in '{config.SaveDirectory}'");
            linesWritten = true;
        }
            continue;
    }

    (string quicksaveFilePath, DateTime quicksaveFileWriteTime) = quicksaveFileCandidates.First();
    quicksaveLastCopy ??= quicksaveFileWriteTime;

    if (quicksaveFileCandidatesCount > 1)
    {
        if (config.VerboseLevel > 0)
        {
            Console.WriteLine($"Found more than one quicksave file in '{config.SaveDirectory}'. " +
            $"Selected '{Path.GetFileName(quicksaveFilePath)}' as it was most recently modified. " +
            $"Candidates were:");
            linesWritten = true;
        }

            string candidates = string.Join("\n  ", quicksaveFileCandidates.Select(x => $"'{Path.GetFileName(x.Item1)}'"));
        Console.WriteLine($"  {candidates}");
    }

    TimeSpan timeSinceLastQuicksave = DateTime.Now.Subtract(quicksaveFileWriteTime);

    // Handles copying (detect when quicksave has changed, copy it to standalone save file)
    if (config.QuicksaveCopy && quicksaveFileCandidatesCount > 0 && quicksaveFileWriteTime != quicksaveLastCopy)
    {
        int highestSaveId = saveFiles
            .Select(file => Regex.Match(Path.GetFileName(file), @"Save(\d+)_.*\.sfs"))
            .Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value))
            .DefaultIfEmpty(0)
            .Max();

        string savePath;
        if (highestSaveId < 100)
            savePath = quicksaveFilePath.Replace("Quicksave0", $"Save100{highestSaveId + 1}");
        else
            savePath = quicksaveFilePath.Replace("Quicksave0", $"Save{highestSaveId + 1}");

        if (config.VerboseLevel > 0)
        {
            Console.WriteLine(
                $"Copying '{quicksaveFilePath.Substring(1 + quicksaveFilePath.LastIndexOf(Path.DirectorySeparatorChar))}' to '{savePath.Substring(1 + savePath.LastIndexOf(Path.DirectorySeparatorChar))}' because quicksave was " +
                $"modified {timeSinceLastQuicksave} ago (at {quicksaveFileWriteTime})");
            linesWritten = true;
        }
        if (TryCopyFile(quicksaveFilePath, savePath))
        {
            quicksaveLastCopy = quicksaveFileWriteTime;
        }
    }

    // Handles saving (detect when it's been an interval after our most recent quicksave, make one)
    if (config.QuicksaveSave && timeSinceLastQuicksave >= TimeSpan.FromSeconds(config.QuicksaveSaveInterval))
    {
        if (config.VerboseLevel > 0)
        {
            Console.WriteLine(
            $"Sending F5 to {config.ProcessName} because quicksave was " +
            $"modified {timeSinceLastQuicksave} ago (at {quicksaveFileWriteTime})");
            linesWritten = true;
        }
            inputSimulator.Keyboard.KeyDown(VirtualKeyCode.F5).Sleep(200).KeyUp(VirtualKeyCode.F5);
    }

    ///////////////////////////////////////////////////////////////////
    // Now delete the oldest, keeping the QuicksaveCount number of files

    IEnumerable<string> oldSaveFiles = Directory.EnumerateFiles(config.SaveDirectory);

    IEnumerable<(string, DateTime)> quicksaveDelFileCandidates = saveFiles
       .Where(x => Path.GetFileName(x).StartsWith("Save1") && Path.GetFileName(x).EndsWith(".sfs") && Path.GetFileName(x)[8] == '_')
       .Select(x => (x, File.GetLastWriteTime(x)))
       .OrderBy(x => x.Item2);

    if (config.VerboseLevel > 3)
    {
        foreach (var f in quicksaveDelFileCandidates)
        {
            Console.WriteLine("quickSaveFile: " + f.Item1 + "   DateTime: " + f.Item2.ToShortTimeString());
        }
        Console.WriteLine(" ");
        linesWritten = true;
    }
    int numFiles = quicksaveDelFileCandidates.Count();
    if (numFiles <= config.QuicksaveCount && config.VerboseLevel > 1)
    {
        Console.WriteLine("No old save files to be deleted");
        linesWritten = true;
    }
    else
    {
        numFiles -= config.QuicksaveCount;
        if (quicksaveDelFileCandidates.Count() > 0)
        {
            string lastFile = quicksaveDelFileCandidates.Last().Item1;
            var i = lastFile.Substring(7);
            var i1 = i.Substring(0, i.IndexOf('_'));

            foreach (var f in quicksaveDelFileCandidates)
            {
                if (--numFiles <= 0)
                    break;

                if (config.VerboseLevel > 0)
                {
                    Console.WriteLine("Deleting old file: " + f.Item1.Substring(1 + f.Item1.LastIndexOf(Path.DirectorySeparatorChar)));
                    linesWritten = true;
                }
                    System.IO.File.Delete(f.Item1);

            }
            // Now need to rename existing files
            saveFiles = Directory.EnumerateFiles(config.SaveDirectory);

            IEnumerable<(string, DateTime)> renamedSaveFiles = saveFiles
               .Where(x => Path.GetFileName(x).StartsWith("Save1") && Path.GetFileName(x).EndsWith(".sfs") && Path.GetFileName(x)[8] == '_')
               .Select(x => (x, File.GetLastWriteTime(x)))
               .OrderBy(x => x.Item2);

            int startSaveId = 1001;
            foreach (var f in renamedSaveFiles)
            {
                int iStart = f.Item1.IndexOf("Save1");
                string saveFileName = f.Item1.Substring(iStart, 8);

                var newSavePath = f.Item1.Replace(saveFileName, $"Save{startSaveId}");
                if (config.VerboseLevel > 1)
                {
                    Console.WriteLine("Old filename: " + saveFileName + ", newSavePath: " + newSavePath + ", startSaveId: " + startSaveId);
                    linesWritten = true;
                }
                    if (f.Item1 != newSavePath)
                    System.IO.File.Move(f.Item1, newSavePath);
                startSaveId++;

            }
        }
    }
    if (linesWritten )
        Console.WriteLine(" ");

}

bool TryCopyFile(string source, string dest)
{
    try
    {
        using FileStream original = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.None);
        using FileStream copy = File.Create(dest);
        original.CopyTo(copy);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"A wild {ex.GetType().Name} appeared when copying {source} to {dest}! It used {ex.Message}. It's super effective!");
        return false;
    }

    return true;
}

// Verbose level Def
//
// 0 = no output after initial settings
// 1 = normal output
// 2 = More verbose output, with full filenames, etc

record Config(
    string SaveDirectory,
    string ProcessName = "Starfield",
    float UpdateInterval = 10.0f,
    bool QuicksaveSave = true,
    float QuicksaveSaveInterval = 120.0f,
    bool QuicksaveCopy = true,
    int QuicksaveCount = 10,
    int VerboseLevel = 1
    );