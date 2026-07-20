using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DocSets
{
    /// <summary>
    /// Тонкая оболочка над системным диалогом Windows. Весь интерфейс, навигация,
    /// адресная строка, значки и масштабирование предоставляются оболочкой Windows.
    /// </summary>
    internal static class NativeFolderDialog
    {
        public static string Show(IWin32Window owner, string title, string initialDirectory)
        {
            IFileOpenDialog dialog = null;
            IShellItem initialItem = null;
            IShellItem selectedItem = null;
            try
            {
                dialog = (IFileOpenDialog)Activator.CreateInstance(typeof(FileOpenDialogCom));
                dialog.GetOptions(out var options);
                dialog.SetOptions(options |
                    FileOpenOptions.PickFolders |
                    FileOpenOptions.ForceFileSystem |
                    FileOpenOptions.PathMustExist |
                    FileOpenOptions.NoChangeDirectory);
                dialog.SetTitle(title ?? "Выбор каталога");

                if (!string.IsNullOrWhiteSpace(initialDirectory) &&
                    SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero,
                        typeof(IShellItem).GUID, out initialItem) == 0)
                {
                    // SetFolder раскрывает системную навигацию на исходном каталоге.
                    dialog.SetFolder(initialItem);
                }

                var result = dialog.Show(owner?.Handle ?? IntPtr.Zero);
                if (result == ErrorCancelled) return null;
                Marshal.ThrowExceptionForHR(result);

                dialog.GetResult(out selectedItem);
                selectedItem.GetDisplayName(ShellItemDisplayName.FileSystemPath, out var pathPointer);
                try
                {
                    return Marshal.PtrToStringUni(pathPointer);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pathPointer);
                }
            }
            finally
            {
                Release(selectedItem);
                Release(initialItem);
                Release(dialog);
            }
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }

        private const int ErrorCancelled = unchecked((int)0x800704C7);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            string path,
            IntPtr bindContext,
            [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

        [Flags]
        private enum FileOpenOptions : uint
        {
            PickFolders = 0x00000020,
            ForceFileSystem = 0x00000040,
            NoChangeDirectory = 0x00000008,
            PathMustExist = 0x00000800
        }

        private enum ShellItemDisplayName : uint
        {
            FileSystemPath = 0x80058000
        }

        [ComImport]
        [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private sealed class FileOpenDialogCom
        {
        }

        [ComImport]
        [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr filterSpecification);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(FileOpenOptions options);
            void GetOptions(out FileOpenOptions options);
            void SetDefaultFolder(IShellItem shellItem);
            void SetFolder(IShellItem shellItem);
            void GetFolder(out IShellItem shellItem);
            void GetCurrentSelection(out IShellItem shellItem);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem shellItem);
            void AddPlace(IShellItem shellItem, int alignment);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IntPtr items);
            void GetSelectedItems(out IntPtr items);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(ShellItemDisplayName displayName, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem shellItem, uint hint, out int order);
        }
    }
}
