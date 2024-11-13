using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RefreshResources
{
	internal static class CakeToolsCleaner
	{
		// This is the folder where Cake stores addins and tools by default
		const string DEFAULT_TOOLS_FOLDER = "./tools";

		[DllImport("kernel32")]
		private static extern long WritePrivateProfileString(string name, string key, string val, string filePath);

		[DllImport("kernel32")]
		private static extern int GetPrivateProfileString(string section, string key, string def, StringBuilder retVal, int size, string filePath);

		internal static void Clean(string rootFolder)
		{
			var folders = Directory.GetDirectories(rootFolder, "*", SearchOption.TopDirectoryOnly);

			// Loop through all folders in the root folder and clean up Cake tools
			// It's important to skip the RefreshResources folder because files may be in use by the script that invoked this tool
			foreach (var folder in folders.Where(path => !Path.GetFileName(path).Equals("RefreshResources", StringComparison.OrdinalIgnoreCase)))
			{
				Console.WriteLine($"Cleaning Cake tools in: {Path.GetFileName(folder)}");
				CleanCakeTools(folder);
			}
		}

		private static void CleanCakeTools(string folder)
		{
			var toolsPath = DEFAULT_TOOLS_FOLDER;

			var files = Directory.GetFiles(folder, "cake.config", SearchOption.TopDirectoryOnly);
			if (files.Length > 0)
			{
				var sb = new StringBuilder(255);
				var _ = GetPrivateProfileString("Paths", "Tools", DEFAULT_TOOLS_FOLDER, sb, 255, files[0]);
				toolsPath = sb.ToString();
				if (string.IsNullOrEmpty(toolsPath)) toolsPath = DEFAULT_TOOLS_FOLDER;
			}

			var fullToolsPath = Path.GetFullPath(Path.Combine(folder, toolsPath));
			if (Directory.Exists(fullToolsPath))
			{
				Directory.Delete(fullToolsPath, true);
				Console.WriteLine($"Deleted: {fullToolsPath}");
			}
		}
	}
}
