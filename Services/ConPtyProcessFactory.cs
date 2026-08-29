using EasyWindowsTerminalControl.Internals;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KaneCode.Services;

/// <summary>
/// Starts a process attached to a ConPTY without allowing Windows to copy the
/// host process's redirected standard handles into the ConPTY client.
/// </summary>
internal sealed class ConPtyProcessFactory : IProcessFactory
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;

    public IProcess Start(string command, nuint attributes, PseudoConsole console, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(console);

        nuint attributeListSize = 0;
        _ = NativeMethods.InitializeProcThreadAttributeList(
            IntPtr.Zero,
            1,
            0,
            ref attributeListSize);

        if (attributeListSize == 0)
        {
            throw CreateWin32Exception("Could not calculate the ConPTY process attribute-list size.");
        }

        IntPtr attributeList = Marshal.AllocHGlobal(checked((int)attributeListSize));
        bool attributeListInitialized = false;
        try
        {
            bool initialized = NativeMethods.InitializeProcThreadAttributeList(
                attributeList,
                1,
                0,
                ref attributeListSize);

            if (!initialized)
            {
                throw CreateWin32Exception("Could not initialize the ConPTY process attribute list.");
            }

            attributeListInitialized = true;
            bool updated = NativeMethods.UpdateProcThreadAttribute(
                attributeList,
                0,
                attributes,
                console.GetDangerousHandle,
                (nuint)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero);

            if (!updated)
            {
                throw CreateWin32Exception("Could not attach the process to the pseudo-console.");
            }

            NativeStartupInfoEx startupInfo = new NativeStartupInfoEx
            {
                StartupInfo = new NativeStartupInfo
                {
                    Cb = (uint)Marshal.SizeOf<NativeStartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = IntPtr.Zero,
                    StandardOutput = IntPtr.Zero,
                    StandardError = IntPtr.Zero
                },
                AttributeList = attributeList
            };

            StringBuilder commandLine = new StringBuilder(command, command.Length + 1);
            bool created = NativeMethods.CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                ExtendedStartupInfoPresent,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out NativeProcessInformation processInformation);

            if (!created)
            {
                throw CreateWin32Exception($"Could not start terminal command '{command}'.");
            }

            return CreateProcess(processInformation);
        }
        finally
        {
            if (attributeListInitialized)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
            }

            Marshal.FreeHGlobal(attributeList);
        }
    }

    private static IProcess CreateProcess(NativeProcessInformation processInformation)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(checked((int)processInformation.ProcessId));

            // Force Process to acquire its own handle before releasing the handles
            // returned by CreateProcess.
            _ = process.Handle;
            return new ConPtyProcess(process);
        }
        catch
        {
            process?.Dispose();
            throw;
        }
        finally
        {
            if (processInformation.ThreadHandle != IntPtr.Zero)
            {
                _ = NativeMethods.CloseHandle(processInformation.ThreadHandle);
            }

            if (processInformation.ProcessHandle != IntPtr.Zero)
            {
                _ = NativeMethods.CloseHandle(processInformation.ProcessHandle);
            }
        }
    }

    private static Win32Exception CreateWin32Exception(string message)
    {
        int errorCode = Marshal.GetLastWin32Error();
        return new Win32Exception(errorCode, message);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeStartupInfo
    {
        public uint Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeStartupInfoEx
    {
        public NativeStartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    private sealed class ConPtyProcess : IProcess
    {
        private readonly Process _process;

        public ConPtyProcess(Process process)
        {
            _process = process;
        }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public void WaitForExit()
        {
            _process.WaitForExit();
        }

        public void Kill(bool entireProcessTree = false)
        {
            if (!HasExited)
            {
                _process.Kill(entireProcessTree);
            }
        }

        public void Dispose()
        {
            _process.Dispose();
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            int flags,
            ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            nuint attribute,
            IntPtr value,
            nuint size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessW(
            string? applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref NativeStartupInfoEx startupInfo,
            out NativeProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
