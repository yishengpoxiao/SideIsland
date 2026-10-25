param([string]$ManifestPath = (Join-Path $PSScriptRoot 'AppxManifest.xml'))
$ErrorActionPreference = 'Stop'
$ManifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path
# Windows' own read-only manifest parser validates schema; no SDK or registration is needed.
if (-not ('CodexIslandPackaging.ManifestValidator' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
namespace CodexIslandPackaging
{
    [ComImport, Guid("beb94909-e451-438b-b5a7-d79e767b75d8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAppxFactory
    {
        [PreserveSig] int CreatePackageWriter(IntPtr output, IntPtr settings, out IntPtr writer);
        [PreserveSig] int CreatePackageReader(IStream input, out IntPtr reader);
        [PreserveSig] int CreateManifestReader(IStream input, out IntPtr reader);
    }
    public static class ManifestValidator
    {
        [DllImport("shlwapi.dll", CharSet=CharSet.Unicode, PreserveSig=true)]
        private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes, bool create, IntPtr template, out IStream stream);
        public static void Validate(string path)
        {
            IStream stream = null;
            object factory = null;
            IntPtr reader = IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, 0x40, 0, false, IntPtr.Zero, out stream));
                factory = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("5842a140-ff9f-4166-8f5c-62f5b7b0c781")));
                Marshal.ThrowExceptionForHR(((IAppxFactory)factory).CreateManifestReader(stream, out reader));
            }
            finally
            {
                if (reader != IntPtr.Zero) Marshal.Release(reader);
                if (factory != null) Marshal.FinalReleaseComObject(factory);
                if (stream != null) Marshal.FinalReleaseComObject(stream);
            }
        }
    }
}
'@
}
[CodexIslandPackaging.ManifestValidator]::Validate($ManifestPath)
Write-Output ('Windows AppxPackaging validated: ' + $ManifestPath)
