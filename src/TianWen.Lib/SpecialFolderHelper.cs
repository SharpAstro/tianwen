using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace TianWen.Lib;

public static class SpecialFolderHelper
{
    internal const string ApplicationName = "TianWen";

    extension (Environment.SpecialFolder folder)
    {
        public DirectoryInfo CreateAppSubFolder() =>
            new DirectoryInfo(Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create)).CreateSubdirectory(ApplicationName);

        public bool TryGetOrCreateAppSubFolder([NotNullWhen(true)] out DirectoryInfo? subDir)
        {
            try
            {
                subDir = folder.CreateAppSubFolder();

                return true;
            }
            catch (Exception)
            {
                subDir = null;
                return false;
            }
        }
    }
}
