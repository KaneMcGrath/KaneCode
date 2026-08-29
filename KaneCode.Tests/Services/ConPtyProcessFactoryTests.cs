using EasyWindowsTerminalControl.Internals;
using KaneCode.Services;
using System.IO;

namespace KaneCode.Tests.Services;

public sealed class ConPtyProcessFactoryTests
{
    [Fact]
    public async Task Start_RoutesPowerShellOutputThroughPseudoConsole()
    {
        const string Marker = "KANECODE_CONPTY_OUTPUT_CAPTURED";
        const nuint PseudoConsoleAttribute = 0x00020016;
        using PseudoConsolePipe inputPipe = new PseudoConsolePipe();
        using PseudoConsolePipe outputPipe = new PseudoConsolePipe();
        using PseudoConsole pseudoConsole = PseudoConsole.Create(
            inputPipe.ReadSide,
            outputPipe.WriteSide,
            80,
            30);

        ConPtyProcessFactory processFactory = new ConPtyProcessFactory();
        string command = $"powershell.exe -NoLogo -NoProfile -NonInteractive -Command \"Write-Output '{Marker}'\"";
        using IProcess process = processFactory.Start(
            command,
            PseudoConsoleAttribute,
            pseudoConsole,
            Path.GetTempPath());

        // The host no longer needs its copies of the pipe ends handed to ConPTY.
        // Closing them also lets ReadToEnd observe EOF after PowerShell exits.
        inputPipe.ReadSide.Dispose();
        outputPipe.WriteSide.Dispose();

        using FileStream outputStream = new FileStream(outputPipe.ReadSide, FileAccess.Read);
        using StreamReader outputReader = new StreamReader(outputStream);
        Task<string> readTask = outputReader.ReadToEndAsync();

        process.WaitForExit();
        pseudoConsole.Dispose();
        string terminalOutput = await readTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Contains(Marker, terminalOutput, StringComparison.Ordinal);
    }
}
