using System;
using System.IO;

namespace TianWen.Lib;

public static class SpecialFolderHelper
{
    extension (Environment.SpecialFolder folder)
    {
        public DirectoryInfo CreateAppSubFolder(string appName) =>
            new DirectoryInfo(Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create)).CreateSubdirectory(appName);
    }
}
