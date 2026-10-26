using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SwiftGet.App.Services;

public static class FolderPickerHelper
{
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    [CoClass(typeof(FileOpenDialogRCW))]
    private interface NativeFileOpenDialog : IFileOpenDialog
    {
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    [ClassInterface(ClassInterfaceType.None)]
    [TypeLibType(TypeLibTypeFlags.FCanCreate)]
    private class FileOpenDialogRCW
    {
    }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show([In] IntPtr parent);
        void SetFileTypes([In] uint cFileTypes, [In] ref IntPtr rgFilterSpec);
        void SetFileTypeIndex([In] uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise([In, MarshalAs(UnmanagedType.Interface)] IntPtr pfde, out uint pdwCookie);
        void Unadvise([In] uint dwCookie);
        void SetOptions([In] uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi);
        void SetFolder([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi);
        void GetFolder([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void SetFileName([In, MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([In, MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void AddPlace([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi, int fdap);
        void SetDefaultExtension([In, MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close([MarshalAs(UnmanagedType.Error)] int hr);
        void SetClientGuid([In] ref Guid guid);
        void ClearClientData();
        void SetFilter([MarshalAs(UnmanagedType.Interface)] IntPtr pFilter);
        void GetResults([MarshalAs(UnmanagedType.Interface)] out IntPtr ppenum);
        void GetSelectedItems([MarshalAs(UnmanagedType.Interface)] out IntPtr ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler([In] IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        void GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void GetDisplayName([In] uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes([In] uint sfgaoMask, out uint psfgaoAttribs);
        void Compare([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi, [In] uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        [In] IntPtr pbc,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    public static string? PickFolder(IntPtr ownerHwnd, string? initialPath = null)
    {
        // 1. Primary: Native Win32 IFileOpenDialog with FOS_PICKFOLDERS
        try
        {
            var clsid = new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
            var type = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
            var dialog = (IFileOpenDialog)Activator.CreateInstance(type)!;
            dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
            dialog.SetTitle("Select Destination Folder");

            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                var expanded = Environment.ExpandEnvironmentVariables(initialPath);
                if (Directory.Exists(expanded))
                {
                    try
                    {
                        var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                        SHCreateItemFromParsingName(expanded, IntPtr.Zero, iid, out var folderItem);
                        if (folderItem != null)
                        {
                            dialog.SetFolder(folderItem);
                        }
                    }
                    catch
                    {
                        // Ignore folder preset error
                    }
                }
            }

            int hr = dialog.Show(ownerHwnd);
            if (hr == 0) // S_OK
            {
                dialog.GetResult(out IShellItem item);
                if (item != null)
                {
                    item.GetDisplayName(SIGDN_FILESYSPATH, out string path);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        return path;
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Native IFileOpenDialog failed, attempting fallback.");
        }

        // 2. Fallback: Windows.Storage.Pickers.FolderPicker
        try
        {
            FolderPicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.Downloads
            };
            picker.FileTypeFilter.Add("*");

            if (ownerHwnd != IntPtr.Zero)
            {
                InitializeWithWindow.Initialize(picker, ownerHwnd);
            }

            var asyncOp = picker.PickSingleFolderAsync();
            var folder = asyncOp.GetAwaiter().GetResult();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "FolderPicker fallback also failed.");
            return null;
        }
    }
}
