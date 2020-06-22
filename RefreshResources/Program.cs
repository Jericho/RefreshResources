using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol.Core.Types;
using Octokit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace RefreshResources
{
	class Program
	{
		private const string ROOT_FOLDER = "D:\\_build\\";
		private const string SOURCE_FOLDER = ROOT_FOLDER + "resources";
		private const int MAX_NUGET_CONCURENCY = 25; // 25 seems like a safe value but I suspect that nuget allows a much large number of concurrent connections.

		public static readonly Regex AddinReferenceRegex = new Regex("(?<lineprefix>.*?)(?<packageprefix>\\#addin nuget:\\?)(?<referencestring>.*?(?=(?:\")|$))(?<linepostfix>.*)", RegexOptions.Compiled | RegexOptions.Multiline);
		public static readonly Regex ToolReferenceRegex = new Regex("(?<lineprefix>.*?)(?<packageprefix>\\#tool nuget:\\?)(?<referencestring>.*?(?=(?:\")|$))(?<linepostfix>.*)", RegexOptions.Compiled | RegexOptions.Multiline);

		private static readonly IEnumerable<(string Owner, string Project)> PROJECTS = new List<(string, string)>
		{
			( "jericho", "CakeMail.RestClient" ),
			( "Http-Multipart-Data-Parser", "Http-Multipart-Data-Parser" ),
			( "jericho", "Picton" ),
			( "jericho", "Picton.Messaging" ),
			( "jericho", "StrongGrid" ),
			( "jericho", "ZoomNet" )
		};

		private static readonly IDictionary<string, string> LABELS = new Dictionary<string, string>
		{
			{ "Breaking Change", "b60205" },
			{ "Bug", "ee0701" },
			{ "duplicate", "cccccc" },
			{ "help wanted", "128A0C" },
			{ "Improvement", "84b6eb" },
			{ "in progress", "b60205" },
			{ "invalid", "e6e6e6" },
			{ "New Feature", "0052cc" },
			{ "on hold", "e99695" },
			{ "question", "cc317c" },
			{ "wontfix", "ffffff" }
		};

		private static readonly string GITHUB_TOKEN = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
		private static readonly string GITHUB_USERNAME = Environment.GetEnvironmentVariable("GITHUB_USERNAME");
		private static readonly string GITHUB_PASSWORD = Environment.GetEnvironmentVariable("GITHUB_PASSWORD");

		static async Task Main(string[] args)
		{
			try
			{
				// Make sure the expected labels are present on github
				await RefreshGithubLabels().ConfigureAwait(false);

				// Make sure the files in the resources folder are up to date
				await RefreshResourcesAsync().ConfigureAwait(false);

				// Copy resource files to projects
				await CopyResourceFiles().ConfigureAwait(false);
			}

			catch (Exception e)
			{
				Console.WriteLine(e.GetBaseException().Message);
			}

			if (!args.Contains("nopause"))
			{
				// Flush the console key buffer
				while (Console.KeyAvailable) Console.ReadKey(true);

				// Wait for user to press a key
				Console.WriteLine("\r\nPress any key to exit...");
				Console.ReadKey();
			}
		}

		private static async Task RefreshGithubLabels()
		{
			var credentials = !string.IsNullOrEmpty(GITHUB_TOKEN) ? new Octokit.Credentials(GITHUB_TOKEN) : new Octokit.Credentials(GITHUB_USERNAME, GITHUB_PASSWORD);
			var githubClient = new GitHubClient(new ProductHeaderValue("RefreshResources")) { Credentials = credentials };

			Console.WriteLine();
			Console.WriteLine("***** Github labels *****");

			foreach (var project in PROJECTS)
			{
				await RefreshGithubLabels(githubClient, project.Owner, project.Project).ConfigureAwait(false);
			}
		}

		private static async Task RefreshGithubLabels(IGitHubClient githubClient, string ownerName, string projectName)
		{
			var labels = await githubClient.Issue.Labels.GetAllForRepository(ownerName, projectName).ConfigureAwait(false);

			var createdLabels = new List<string>();
			var modifiedLabels = new List<string>();

			foreach (var label in LABELS)
			{
				// Perform case-insensitive search
				var existingLabel = labels.FirstOrDefault(l => l.Name.Equals(label.Key, StringComparison.OrdinalIgnoreCase));

				// Create label if it doesn't already exist
				if (existingLabel == null)
				{
					await githubClient.Issue.Labels.Create(ownerName, projectName, new NewLabel(label.Key, label.Value)).ConfigureAwait(false);
					createdLabels.Add(label.Key);
				}

				// Update the existing label if it doesn't match perfectly (wrong color or inconsistent casing)
				else if (!existingLabel.Name.Equals(label.Key, StringComparison.Ordinal) || (!existingLabel.Color.Equals(label.Value, StringComparison.Ordinal)))
				{
					await githubClient.Issue.Labels.Update(ownerName, projectName, existingLabel.Name, new LabelUpdate(label.Key, label.Value)).ConfigureAwait(false);
					modifiedLabels.Add(label.Key);
				}
			}

			if (createdLabels.Any()) Console.WriteLine($"{projectName} added: " + string.Join(", ", createdLabels));
			if (modifiedLabels.Any()) Console.WriteLine($"{projectName} modified: " + string.Join(", ", modifiedLabels));
			if (!createdLabels.Any() && !modifiedLabels.Any()) Console.WriteLine($"{projectName}: All labels already up to date");
		}

		private static async Task RefreshResourcesAsync()
		{
			var repo = new LibGit2Sharp.Repository(SOURCE_FOLDER);
			var author = repo.Config.BuildSignature(DateTimeOffset.Now);
			var httpClient = new HttpClient();

			var providers = new List<Lazy<INuGetResourceProvider>>();
			providers.AddRange(NuGet.Protocol.Core.Types.Repository.Provider.GetCoreV3());  // Add v3 API support
			var packageSource = new PackageSource("https://api.nuget.org/v3/index.json");
			var sourceRepository = new SourceRepository(packageSource, providers);
			var nugetPackageMetadataClient = sourceRepository.GetResource<PackageMetadataResource>();

			//==================================================
			// STEP 1 - Git pull in case there are some changes in the GitHub repo that have not been pulled (this would be very surprising, but better safe than sorry)
			var pullOptions = new PullOptions()
			{
				FetchOptions = new FetchOptions()
			};
			Commands.Pull(repo, author, pullOptions);

			//==================================================
			// STEP 2 - Refresh the gitignore file
			using (var request = new HttpRequestMessage(HttpMethod.Get, "https://www.gitignore.io/api/visualstudio"))
			{
				var response = await httpClient.SendAsync(request).ConfigureAwait(false);
				var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

				content = content
					.Trim('\n')
					.Replace("# Created by https://www.gitignore.io/api/visualstudio", "# Created with the help of https://www.gitignore.io/api/visualstudio")
					.Replace("# Cake - Uncomment if you are using it\n# tools/**\n# !tools/packages.config", "# Cake - Uncomment if you are using it\ntools/**\n!tools/packages.config")
					.Replace("# End of https://www.gitignore.io/api/visualstudio", "# WinMerge\n*.bak\n\n# End of https://www.gitignore.io/api/visualstudio")
					.Replace("\n", "\r\n");

				File.WriteAllText(Path.Combine(SOURCE_FOLDER, ".gitignore"), content);
			}

			//==================================================
			// STEP 3 - Refresh the Cake bootstrap
			using (var request = new HttpRequestMessage(HttpMethod.Get, "https://raw.githubusercontent.com/cake-build/resources/develop/build.ps1"))
			{
				var response = await httpClient.SendAsync(request).ConfigureAwait(false);
				var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

				content = content.Replace("\n", Environment.NewLine);

				File.WriteAllText(Path.Combine(SOURCE_FOLDER, "build.ps1"), content);
			}

			//==================================================
			// STEP 4 - Make sure the addins referenced in the build script are up to date
			var buildScriptFilePath = Path.Combine(SOURCE_FOLDER, "build.cake");

			var buildScriptContent = await File.ReadAllTextAsync(buildScriptFilePath).ConfigureAwait(false);
			buildScriptContent = buildScriptContent.Replace(Environment.NewLine, "\n");  // '\n' is the EOL for regex 

			var addinsMatchResults = AddinReferenceRegex.Matches(buildScriptContent);
			var toolsMatchResults = ToolReferenceRegex.Matches(buildScriptContent);

			var addinsReferencesInfo = await addinsMatchResults.ForEachAsync(async match => await GetReferencedPackageInfo(match, nugetPackageMetadataClient).ConfigureAwait(false), MAX_NUGET_CONCURENCY).ConfigureAwait(false);
			var toolsReferencesInfo = await toolsMatchResults.ForEachAsync(async match => await GetReferencedPackageInfo(match, nugetPackageMetadataClient).ConfigureAwait(false), MAX_NUGET_CONCURENCY).ConfigureAwait(false);

			var referencesInfo = addinsReferencesInfo.Union(toolsReferencesInfo).OrderBy(r => r.Name).ToArray();

			var updatedBuildScriptContent = AddinReferenceRegex.Replace(buildScriptContent, match => GetPackageReferenceWithLatestVersion(match, referencesInfo));
			updatedBuildScriptContent = ToolReferenceRegex.Replace(updatedBuildScriptContent, match => GetPackageReferenceWithLatestVersion(match, referencesInfo));
			updatedBuildScriptContent = updatedBuildScriptContent.Replace("\n", Environment.NewLine);

			await File.WriteAllTextAsync(buildScriptFilePath, updatedBuildScriptContent).ConfigureAwait(false);


			//==================================================
			// STEP 5 - Commit the changes (if any)
			var changes = repo.Diff.Compare<TreeChanges>();
			if (changes.Any())
			{
				Commands.Stage(repo, changes.Select(c => c.Path));
				var commit = repo.Commit("Refresh resources", author, author);

				var pushOptions = new PushOptions()
				{
					CredentialsProvider = new CredentialsHandler(
					(url, usernameFromUrl, types) =>
					{
						if (!string.IsNullOrEmpty(GITHUB_TOKEN))
						{
							return new UsernamePasswordCredentials() { Username = GITHUB_TOKEN, Password = string.Empty };
						}
						else
						{
							return new UsernamePasswordCredentials() { Username = GITHUB_USERNAME, Password = GITHUB_PASSWORD };
						}
					})
				};
				repo.Network.Push(repo.Branches["master"], pushOptions);
			}

			//==================================================
			// Write summary info to console
			Console.WriteLine();
			Console.WriteLine("***** Cake addins references *****");
			var updatedReferences = referencesInfo.Where(r => r.ReferencedVersion != r.LatestVersion);
			if (updatedReferences.Any())
			{
				Console.WriteLine(string.Join(Environment.NewLine, updatedReferences.Select(r => $"    {r.Name} {r.ReferencedVersion} --> {r.LatestVersion}")));
			}
			else
			{
				Console.WriteLine("    All referenced addins are up to date");
			}

			Console.WriteLine();
			Console.WriteLine("***** Resources committed to github repo *****");
			var modifiedFiles = changes.Added.Union(changes.Modified);
			if (modifiedFiles.Any())
			{
				Console.WriteLine(string.Join(Environment.NewLine, modifiedFiles.Select(c => $"    {c.Path}")));
			}
			else
			{
				Console.WriteLine("All files already up to date");
			}
		}

		private static async Task CopyResourceFiles()
		{
			var files = GetSourceFiles(SOURCE_FOLDER);

			Console.WriteLine();
			Console.WriteLine("***** Project Resources *****");

			foreach (var project in PROJECTS)
			{
				await CopyResourceFilesToProject(files, project.Owner, project.Project).ConfigureAwait(false);
			}
		}

		private static async Task CopyResourceFilesToProject(IEnumerable<FileInfo> resoureFiles, string ownerName, string projectName)
		{
			if (string.IsNullOrEmpty(ownerName)) throw new ArgumentException("You must specify the owner of the project", nameof(ownerName));
			if (string.IsNullOrEmpty(projectName)) throw new ArgumentException("You must specify the name of the project", nameof(projectName));

			var modifiedFiles = new List<string>();

			foreach (var sourceFile in resoureFiles)
			{
				var sourceContent = File.ReadAllText(sourceFile.FullName)
					.Replace("%%PROJECT-NAME%%", projectName);
				var destinationName = sourceFile.FullName.Replace(SOURCE_FOLDER, "").Trim('\\');
				var destinationPath = Path.Combine(ROOT_FOLDER, projectName, destinationName);

				var destinationFile = new FileInfo(destinationPath);

				if (!SameContent(sourceContent, destinationFile))
				{
					modifiedFiles.Add(destinationName);
					await File.WriteAllTextAsync(destinationPath, sourceContent).ConfigureAwait(false);
				}
			}

			if (modifiedFiles.Any())
			{
				Console.WriteLine($"{projectName} " + string.Join(", ", modifiedFiles));
			}
			else
			{
				Console.WriteLine($"{projectName}: All files already up to date");
			}
		}

		private static IEnumerable<FileInfo> GetSourceFiles(string directory)
		{
			var files = Directory
				.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
				.ToArray();

			foreach (var file in files)
			{
				var fi = new FileInfo(file);
				if (!fi.Attributes.HasFlag(FileAttributes.Hidden))
				{
					yield return fi;
				}
			}

			var subFolders = Directory
				.EnumerateDirectories(directory)
				.Where(d => !(new DirectoryInfo(d)).Attributes.HasFlag(FileAttributes.Hidden));

			foreach (var subFolder in subFolders)
			{
				foreach (var f in GetSourceFiles(subFolder))
				{
					yield return f;
				}
			}
		}

		private static bool SameContent(string content, FileInfo destination)
		{
			if (!destination.Exists)
				return false;

			if (content.Length != destination.Length)
				return false;

			var sourceContent = Encoding.UTF8.GetBytes(content);
			var destinationContent = File.ReadAllBytes(destination.FullName);

			var areEqual = new ReadOnlySpan<byte>(sourceContent).SequenceEqual(destinationContent);

			return areEqual;
		}

		private static async Task<string> GetLatestNugetPackageVersion(string packageName, PackageMetadataResource nugetPackageMetadataClient)
		{
			var searchMetadata = await nugetPackageMetadataClient.GetMetadataAsync(packageName, false, false, new SourceCacheContext(), NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

			IPackageSearchMetadata latestPackage = null;
			if (searchMetadata != null && searchMetadata.Any())
			{
				if (searchMetadata.All(p => p.Identity?.HasVersion ?? false))
				{
					latestPackage = searchMetadata.OrderByDescending(p => p.Identity.Version).FirstOrDefault();
				}
				else
				{
					latestPackage = searchMetadata.OrderByDescending(p => p.Published).FirstOrDefault();
				}
			}

			if (latestPackage == null)
			{
				throw new Exception($"Unable to find package '{packageName}'");
			}

			var version = latestPackage.Identity.Version.ToNormalizedString();
			return version;
		}

		private static async Task<(string Name, string ReferencedVersion, string LatestVersion)> GetReferencedPackageInfo(Match match, PackageMetadataResource nugetPackageMetadataClient)
		{
			var parameters = HttpUtility.ParseQueryString(match.Groups["referencestring"].Value);
			var packageName = parameters["package"];
			var referencedVersion = parameters["version"];
			var latestVersion = await GetLatestNugetPackageVersion(packageName, nugetPackageMetadataClient).ConfigureAwait(false);

			return (packageName, referencedVersion, latestVersion);
		}

		private static string GetPackageReferenceWithLatestVersion(Match match, IEnumerable<(string Name, string ReferencedVersion, string LatestVersion)> referencesInfo)
		{
			var parameters = HttpUtility.ParseQueryString(match.Groups["referencestring"].Value);

			// These are the supported parameters as documented here: https://cakebuild.net/docs/fundamentals/preprocessor-directives
			var packageName = parameters["package"];
			var referencedVersion = parameters["version"];
			var loadDependencies = parameters["loaddependencies"];
			var include = parameters["include"];
			var exclude = parameters["exclude"];
			var prerelease = parameters.AllKeys.Contains("prerelease");

			var packageLatestVersion = referencesInfo.First(r => r.Name == packageName).LatestVersion;

			var newContent = new StringBuilder();
			newContent.Append(match.Groups["lineprefix"].Value);
			newContent.Append(match.Groups["packageprefix"].Value);
			newContent.AppendFormat("package={0}", packageName);
			newContent.AppendFormat("&version={0}", packageLatestVersion);
			if (!string.IsNullOrEmpty(loadDependencies)) newContent.AppendFormat("&loaddependencies={0}", loadDependencies);
			if (!string.IsNullOrEmpty(include)) newContent.AppendFormat("&include={0}", include);
			if (!string.IsNullOrEmpty(exclude)) newContent.AppendFormat("&exclude={0}", exclude);
			if (prerelease) newContent.Append("&prerelease");
			newContent.Append(match.Groups["linepostfix"].Value);

			return newContent.ToString();
		}
	}
}
