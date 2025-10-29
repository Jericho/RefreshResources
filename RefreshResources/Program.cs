using HtmlAgilityPack;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using NuGet.Common;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Octokit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;

namespace RefreshResources
{
	partial class Program
	{
		private const string ROOT_FOLDER = "D:\\_build\\";
		private const string SOURCE_FOLDER = ROOT_FOLDER + "resources";
		private const int MAX_NUGET_CONCURENCY = 25; // 25 seems like a safe value but I suspect nuget allows a much large number of concurrent connections.
		private const int DESIRED_SDK_MAJOR_VERSION = 10;

		private static readonly Regex _addinReferenceRegex = new(string.Format(ADDIN_REFERENCE_REGEX, "addin"), RegexOptions.Compiled | RegexOptions.Multiline);
		private static readonly Regex _toolReferenceRegex = new(string.Format(ADDIN_REFERENCE_REGEX, "tool"), RegexOptions.Compiled | RegexOptions.Multiline);
		private static readonly Regex _loadReferenceRegex = new(string.Format(ADDIN_REFERENCE_REGEX, "(load|l)"), RegexOptions.Compiled | RegexOptions.Multiline);

		private const string ADDIN_REFERENCE_REGEX = "(?<lineprefix>.*)(?<packageprefix>\\#{0}) (?<scheme>(nuget|dotnet)):(?<separator1>\"?)(?<packagerepository>.*)\\?(?<referencestring>.*?(?=(?:[\"| ])|$))(?<separator2>\"?)(?<separator3> ?)(?<linepostfix>.*?$)";

		private enum ProjectType
		{
			Library,
			CakeAddin,
		}

		private static readonly List<(string GitHubOwner, string GitHubRepo, ProjectType ProjectType)> PROJECTS =
		[
			( "Http-Multipart-Data-Parser", "HttpMultipartParser", ProjectType.Library),
			( "jericho", "Picton", ProjectType.Library),
			( "jericho", "Picton.Messaging", ProjectType.Library),
			( "jericho", "StrongGrid", ProjectType.Library),
			( "jericho", "ZoomNet", ProjectType.Library),
			( "cake-contrib", "Cake.Email.Common", ProjectType.CakeAddin),
			( "cake-contrib", "Cake.Email", ProjectType.CakeAddin),
			( "cake-contrib", "Cake.SendGrid", ProjectType.CakeAddin),
		];

		private static readonly List<(string Name, string Color, string Description)> LABELS =
		[
			( "Breaking Change", "b60205", "This change causes backward compatibility issue(s)" ),
			( "Bug", "d73a4a", "This change resolves a defect" ),
			( "Documentation", "0075ca", "Improvements or additions to documentation" ),
			( "duplicate", "cfd3d7", "This issue or pull request already exists" ),
			( "Enhancement", "a2eeef", "New feature or request" ),
			( "good first issue", "7057ff", "Good for newcomers" ),
			( "help wanted", "008672", "Extra attention is needed" ),
			( "in progress", "b60205", "Someone is working on this" ),
			( "invalid", "e4e669", "This doesn't seem right" ),
			( "on hold", "e99695", "This will not be worked on until further notice" ),
			( "question", "d876e3", "Someone is asking a question" ),
			( "wontfix", "ffffff", "This will not be worked on" )
		];

		private static readonly string GITHUB_TOKEN = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
		private static readonly string GITHUB_USERNAME = Environment.GetEnvironmentVariable("GITHUB_USERNAME");
		private static readonly string GITHUB_PASSWORD = Environment.GetEnvironmentVariable("GITHUB_PASSWORD");

		static async Task Main(string[] args)
		{
			try
			{
				if (args.Contains("cleantools"))
				{
					CakeToolsCleaner.Clean(ROOT_FOLDER);
				}
				else
				{
					var credentials = !string.IsNullOrEmpty(GITHUB_TOKEN) ? new Octokit.Credentials(GITHUB_TOKEN) : new Octokit.Credentials(GITHUB_USERNAME, GITHUB_PASSWORD);
					var githubClient = new GitHubClient(new ProductHeaderValue("RefreshResources")) { Credentials = credentials };

					await RefreshGithubLabels(githubClient).ConfigureAwait(false);
					await RefreshSendGridWebHookList(githubClient).ConfigureAwait(false);
					await RefreshResourcesAsync().ConfigureAwait(false);
					await CopyResourceFiles().ConfigureAwait(false);
				}
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

		private static async Task RefreshGithubLabels(GitHubClient githubClient)
		{
			Console.WriteLine();
			Console.WriteLine("***** Github labels *****");

			foreach (var project in PROJECTS)
			{
				await RefreshGithubLabels(githubClient, project.GitHubOwner, project.GitHubRepo).ConfigureAwait(false);
			}
		}

		private static async Task RefreshGithubLabels(GitHubClient githubClient, string ownerName, string repoName)
		{
			var existingLabels = await githubClient.Issue.Labels.GetAllForRepository(ownerName, repoName).ConfigureAwait(false);

			var createdLabels = new List<string>();
			var modifiedLabels = new List<string>();

			foreach (var label in LABELS)
			{
				// Perform case-insensitive search
				var existingLabel = existingLabels.FirstOrDefault(l => l.Name.Equals(label.Name, StringComparison.OrdinalIgnoreCase));

				// Create label if it doesn't already exist
				if (existingLabel == null)
				{
					var newLabel = new NewLabel(label.Name, label.Color)
					{
						Description = label.Description
					};

					await githubClient.Issue.Labels.Create(ownerName, repoName, newLabel).ConfigureAwait(false);
					createdLabels.Add(label.Name);
				}

				// Update the existing label if it doesn't match what's expected
				else
				{
					var nameMatches = existingLabel.Name.Equals(label.Name, StringComparison.Ordinal);
					var colorMatches = existingLabel.Color.Equals(label.Color, StringComparison.Ordinal);
					var descriptionMatches = string.IsNullOrEmpty(label.Description) || (existingLabel.Description?.Equals(label.Description, StringComparison.Ordinal) ?? false);

					if (!nameMatches || !colorMatches || !descriptionMatches)
					{
						var labelUpdate = new LabelUpdate(label.Name, label.Color)
						{
							Description = label.Description
						};
						await githubClient.Issue.Labels.Update(ownerName, repoName, existingLabel.Name, labelUpdate).ConfigureAwait(false);
						modifiedLabels.Add(label.Name);
					}
				}
			}

			if (createdLabels.Count > 0) Console.WriteLine($"{repoName} added: " + string.Join(", ", createdLabels));
			if (modifiedLabels.Count > 0) Console.WriteLine($"{repoName} modified: " + string.Join(", ", modifiedLabels));
			if (createdLabels.Count == 0 && modifiedLabels.Count == 0) Console.WriteLine($"{repoName}: All labels already up to date");
		}

		private static async Task RefreshResourcesAsync(CancellationToken cancellationToken = default)
		{
			Console.WriteLine();
			Console.WriteLine("***** Resources *****");

			var repo = new LibGit2Sharp.Repository(SOURCE_FOLDER);
			var author = repo.Config.BuildSignature(DateTimeOffset.Now);
			var httpClient = new HttpClient();


			//==================================================
			// STEP 1 - Git pull in case there are some changes in the GitHub repo that have not been pulled (this would be very surprising, but better safe than sorry)
			var pullOptions = new PullOptions()
			{
				FetchOptions = new FetchOptions()
			};
			Commands.Pull(repo, author, pullOptions);


			//==================================================
			// STEP 2 - Refresh the gitignore file
			using (var request = new HttpRequestMessage(HttpMethod.Get, "https://www.toptal.com/developers/gitignore/api/visualstudio"))
			{
				var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
				var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

				content = content
					.Trim('\n')
					.Replace("# Created by https://www.toptal.com/developers/gitignore/api/visualstudio", "# Created with the help of https://www.toptal.com/developers/gitignore/api/visualstudio (formerly https://www.gitignore.io/api/visualstudio)")
					.Replace("# Cake - Uncomment if you are using it\n# tools/**\n# !tools/packages.config", "# Cake\n.cake/**\ntools/**\nBuildArtifacts/")
					.Replace("# End of https://www.toptal.com/developers/gitignore/api/visualstudio", "# WinMerge\n*.bak\n\n# End of https://www.toptal.com/developers/gitignore/api/visualstudio")
					.Replace("\n", Environment.NewLine);

				await File.WriteAllTextAsync(Path.Combine(SOURCE_FOLDER, ".gitignore"), content, cancellationToken).ConfigureAwait(false);
			}


			//==================================================
			// STEP 3 - Refresh other files (such as the dotnet install scripts for example)
			var bootstrapFiles = new (string source, string desiredLineEnding)[]
			{
				("https://raw.githubusercontent.com/cake-build/resources/master/dotnet-tool/build.ps1", "\r\n"),
				("https://raw.githubusercontent.com/cake-build/resources/master/dotnet-tool/build.sh", "\n")
			};

			foreach (var (source, desiredLineEnding) in bootstrapFiles)
			{
				var destinationFileName = Path.GetFileName(source);

				using var request = new HttpRequestMessage(HttpMethod.Get, source);
				var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
				var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

				content = content
					.Replace("\r\n", "\n")
					.Replace("\n", desiredLineEnding);

				await File.WriteAllTextAsync(Path.Combine(SOURCE_FOLDER, destinationFileName), content, cancellationToken).ConfigureAwait(false);
			}


			//==================================================
			// STEP 4 - Make sure the addins referenced in the build script are up to date
			var buildScriptFilePath = Path.Combine(SOURCE_FOLDER, "build.cake");

			var buildScriptContent = await File.ReadAllTextAsync(buildScriptFilePath, cancellationToken).ConfigureAwait(false);
			buildScriptContent = buildScriptContent.Replace(Environment.NewLine, "\n");  // '\n' is the EOL for regex 

			var addinsMatchResults = _addinReferenceRegex.Matches(buildScriptContent);
			var toolsMatchResults = _toolReferenceRegex.Matches(buildScriptContent);
			var loadsMatchResults = _loadReferenceRegex.Matches(buildScriptContent);

			var addinsReferencesInfo = await addinsMatchResults.ForEachAsync(async match => await GetReferencedPackageInfo(match).ConfigureAwait(false), MAX_NUGET_CONCURENCY).ConfigureAwait(false);
			var toolsReferencesInfo = await toolsMatchResults.ForEachAsync(async match => await GetReferencedPackageInfo(match).ConfigureAwait(false), MAX_NUGET_CONCURENCY).ConfigureAwait(false);
			var loadsReferencesInfo = await loadsMatchResults.ForEachAsync(async match => await GetReferencedPackageInfo(match).ConfigureAwait(false), MAX_NUGET_CONCURENCY).ConfigureAwait(false);

			var referencesInfo = addinsReferencesInfo
				.Union(toolsReferencesInfo)
				.Union(loadsReferencesInfo)
				.OrderBy(r => r.Name).ToArray();

			var updatedBuildScriptContent = _addinReferenceRegex.Replace(buildScriptContent, match => GetPackageReferenceWithLatestVersion(match, referencesInfo));
			updatedBuildScriptContent = _toolReferenceRegex.Replace(updatedBuildScriptContent, match => GetPackageReferenceWithLatestVersion(match, referencesInfo));
			updatedBuildScriptContent = _loadReferenceRegex.Replace(updatedBuildScriptContent, match => GetPackageReferenceWithLatestVersion(match, referencesInfo));
			updatedBuildScriptContent = updatedBuildScriptContent.Replace("\n", Environment.NewLine);

			await File.WriteAllTextAsync(buildScriptFilePath, updatedBuildScriptContent, cancellationToken).ConfigureAwait(false);


			//==================================================
			// STEP 5 - Update global.json with desired .NET SDK version
			var latestSdkVersion = await GetLatestSdkVersion(DESIRED_SDK_MAJOR_VERSION, cancellationToken).ConfigureAwait(false);
			var globalJsonFilePath = Path.Combine(SOURCE_FOLDER, "global.json");
			string currentGlobalJsonContent;

			using (var sr = new StreamReader(globalJsonFilePath))
			{
				currentGlobalJsonContent = await sr.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
			}

			var currentSdkInfo = Extensions.DeserializeAnonymousType(currentGlobalJsonContent, new { sdk = new { version = "", rollForward = "", allowPrerelease = false }, test = new { runner = "Microsoft.Testing.Platform" } });

			var updatedSdkInfo = new
			{
				sdk = new
				{
					version = latestSdkVersion.ToString(),
					currentSdkInfo.sdk.rollForward,
					currentSdkInfo.sdk.allowPrerelease
				},
				test = new
				{
					currentSdkInfo.test.runner
				}
			};

			var updatedGlobalJsonContent = JsonSerializer.Serialize(updatedSdkInfo, new JsonSerializerOptions() { WriteIndented = true });
			using (var sw = new StreamWriter(globalJsonFilePath))
			{
				await sw.WriteAsync(updatedGlobalJsonContent).ConfigureAwait(false);
			}


			//==================================================
			// STEP 6 - Commit the changes (if any)
			var changes = repo.Diff.Compare<TreeChanges>();
			if (changes.Count > 0)
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
				repo.Network.Push(repo.Branches["main"], pushOptions);
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

		private static async Task<SemVersion> GetLatestSdkVersion(int desiredSdkMajorVersion, CancellationToken cancellationToken)
		{
			var htmlParser = new HtmlWeb();
			var htmlDoc = await htmlParser.LoadFromWebAsync($"https://dotnet.microsoft.com/en-us/download/dotnet/{desiredSdkMajorVersion}.0", cancellationToken).ConfigureAwait(false);
			var latestSdkVersion = htmlDoc.DocumentNode
				.SelectNodes("//h3")
				.Where(node => node.Id.StartsWith($"sdk-{desiredSdkMajorVersion}", StringComparison.OrdinalIgnoreCase))
				.Select(node => SemVersion.Parse(node.InnerText.Replace("SDK ", string.Empty)))
				.OrderByDescending(version => version)
				.First();

			return latestSdkVersion;
		}

		private static async Task CopyResourceFiles()
		{
			var files = GetSourceFiles(SOURCE_FOLDER);

			Console.WriteLine();
			Console.WriteLine("***** Project Resources *****");

			foreach (var project in PROJECTS)
			{
				var filesForThisProject = files
					.Where(fi =>
					{
						// Pick the right cake script depending on the type of project
						if (fi.Name.Equals("build.cake", StringComparison.OrdinalIgnoreCase)) return project.ProjectType == ProjectType.Library;
						if (fi.Name.Equals("recipe.cake", StringComparison.OrdinalIgnoreCase)) return project.ProjectType == ProjectType.CakeAddin;

						// Pick the right GitVersion config file depending on the type of project
						if (fi.Name.Equals("GitVersion.yml", StringComparison.OrdinalIgnoreCase)) return project.ProjectType == ProjectType.Library;
						if (fi.Name.Equals("GitVersion-old.yml", StringComparison.OrdinalIgnoreCase)) return project.ProjectType == ProjectType.CakeAddin;

						// I am using Microsoft's CodeCoverage tool in my library projects only at this time
						if (fi.Name.Equals("CodeCoverage.runsettings", StringComparison.OrdinalIgnoreCase)) return project.ProjectType == ProjectType.Library;

						return true;
					})
					.ToArray();

				await CopyResourceFilesToProject(filesForThisProject, project).ConfigureAwait(false);
			}
		}

		private static async Task CopyResourceFilesToProject(IEnumerable<FileInfo> resourceFiles, (string GitHubOwner, string GitHubRepoName, ProjectType ProjectType) project)
		{
			ArgumentNullException.ThrowIfNullOrEmpty(project.GitHubOwner, $"{nameof(project)}.{nameof(project.GitHubOwner)}");
			ArgumentNullException.ThrowIfNullOrEmpty(project.GitHubRepoName, $"{nameof(project)}.{nameof(project.GitHubRepoName)}");

			var buildTargetName = project.ProjectType switch
			{
				ProjectType.Library => "AppVeyor",
				ProjectType.CakeAddin => "CI",
				_ => throw new Exception("Unknown project type")
			};

			var cakeScriptFileName = project.ProjectType switch
			{
				ProjectType.Library => "build.cake",
				ProjectType.CakeAddin => "recipe.cake",
				_ => throw new Exception("Unknown project type")
			};

			var buildCakeVersion = project.ProjectType switch
			{
				ProjectType.Library => "5.1.0",
				ProjectType.CakeAddin => "2.3.0",
				_ => throw new Exception("Unknown project type")
			};

			var buildImages = project.ProjectType switch
			{
				ProjectType.Library => "  - Ubuntu2204\r\n  - Visual Studio 2022",
				ProjectType.CakeAddin => "  - Visual Studio 2022", // I get "could not load ssl libraries" when attempting to build addins on Ubuntu
				_ => throw new Exception("Unknown project type")
			};

			var modifiedFiles = new List<string>();

			foreach (var sourceFile in resourceFiles)
			{
				var fileContent = await File.ReadAllTextAsync(sourceFile.FullName).ConfigureAwait(false);
				var sourceContent = fileContent
					.Replace("%%PROJECT-NAME%%", project.GitHubRepoName)
					.Replace("%%BUILD-TARGET-NAME%%", buildTargetName)
					.Replace("%%CAKE-SCRIPT-FILENAME%%", cakeScriptFileName)
					.Replace("%%BUILD-CAKE-VERSION%%", buildCakeVersion)
					.Replace("%%BUILD-IMAGES%%", buildImages);

				var destinationName = sourceFile.FullName
					.Replace(SOURCE_FOLDER, string.Empty)
					.Replace("-old", string.Empty)
					.Trim('\\');
				var destinationPath = Path.Combine(ROOT_FOLDER, project.GitHubRepoName, destinationName);
				var destinationFolder = Path.GetDirectoryName(destinationPath);

				var destinationFile = new FileInfo(destinationPath);

				if (!SameContent(sourceContent, destinationFile))
				{
					modifiedFiles.Add(destinationName);
					if (!Directory.Exists(destinationFolder)) Directory.CreateDirectory(destinationFolder);
					await File.WriteAllTextAsync(destinationPath, sourceContent).ConfigureAwait(false);
				}
			}

			if (modifiedFiles.Count > 0)
			{
				Console.WriteLine($"{project.GitHubRepoName} " + string.Join(", ", modifiedFiles));
			}
			else
			{
				Console.WriteLine($"{project.GitHubRepoName}: All files already up to date");
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
				.Where(d => !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.Hidden));

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

		private static async Task<NuGetVersion> GetLatestNugetPackageVersion(string packageName, string packageSource, bool includePrerelease)
		{
			var metadataClient = PackageMetadataResourceManager.GetClient(packageSource);
			var searchMetadata = await metadataClient.GetMetadataAsync(packageName, includePrerelease, false, new SourceCacheContext(), NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

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

			return latestPackage?.Identity?.Version;
		}

		private static async Task<(string Name, string ReferencedVersion, string LatestVersion, string LatestPackageSource)> GetReferencedPackageInfo(Match match)
		{
			var parameters = HttpUtility.ParseQueryString(match.Groups["referencestring"].Value);
			var packageRepository = match.Groups["packagerepository"].Value;
			var packageName = parameters["package"];
			var referencedVersion = parameters["version"];

			// Get the latest version from NuGet.org
			var latestVersionFromNuGet = await GetLatestNugetPackageVersion(packageName, null, false).ConfigureAwait(false);

			// Get the latest version from custom source (if applicable)
			var latestVersionFromCustomSource = string.IsNullOrEmpty(packageRepository) ? null : await GetLatestNugetPackageVersion(packageName, packageRepository, true).ConfigureAwait(false);

			// Determine which version is the most recent
			if (latestVersionFromNuGet == null && latestVersionFromCustomSource == null) throw new Exception($"Unable to find package '{packageName}'");
			else if (latestVersionFromCustomSource != null) return (packageName, referencedVersion, latestVersionFromCustomSource.ToNormalizedString(), packageRepository);
			else return (packageName, referencedVersion, latestVersionFromNuGet.ToNormalizedString(), string.Empty);
		}

		private static string GetPackageReferenceWithLatestVersion(Match match, IEnumerable<(string Name, string ReferencedVersion, string LatestVersion, string LatestPackageSource)> referencesInfo)
		{
			var parameters = HttpUtility.ParseQueryString(match.Groups["referencestring"].Value);

			// These are the supported parameters as documented here: https://cakebuild.net/docs/fundamentals/preprocessor-directives
			var packageName = parameters["package"];
			var referencedVersion = parameters["version"];
			var loadDependencies = parameters["loaddependencies"];
			var include = parameters["include"];
			var exclude = parameters["exclude"];
			var prerelease = (parameters.AllKeys?.Contains("prerelease") ?? false) || (parameters.GetValues(null)?.Contains("prerelease") ?? false);

			var latestPackage = referencesInfo.First(r => r.Name == packageName);

			var newContent = new StringBuilder();
			newContent.Append(match.Groups["lineprefix"].Value);
			newContent.Append(match.Groups["packageprefix"].Value);
			newContent.AppendFormat(" {0}:", match.Groups["scheme"].Value);
			newContent.Append(match.Groups["separator1"].Value);
			newContent.Append(latestPackage.LatestPackageSource);
			newContent.AppendFormat("?package={0}", packageName);
			newContent.AppendFormat("&version={0}", latestPackage.LatestVersion);
			if (!string.IsNullOrEmpty(loadDependencies)) newContent.AppendFormat("&loaddependencies={0}", loadDependencies);
			if (!string.IsNullOrEmpty(include)) newContent.AppendFormat("&include={0}", include);
			if (!string.IsNullOrEmpty(exclude)) newContent.AppendFormat("&exclude={0}", exclude);
			if (prerelease) newContent.Append("&prerelease");
			newContent.Append(match.Groups["separator2"].Value);
			newContent.Append(match.Groups["separator3"].Value);
			newContent.Append(match.Groups["linepostfix"].Value);

			return newContent.ToString();
		}

		private static async Task RefreshSendGridWebHookList(GitHubClient githubClient, CancellationToken cancellationToken = default)
		{
			Console.WriteLine();
			Console.WriteLine("***** SendGrid Webhooks list *****");

			var repoOwner = "jericho";
			var repoName = "ZoomNet";

			var resourcePath = $"/Source/{repoName}/Models/Webhooks/EventType.cs";
			var contents = await githubClient.Repository.Content.GetAllContents(repoOwner, repoName, resourcePath).ConfigureAwait(false);
			var eventTypeCSharpSource = contents[0].Content;

			var meetingEvents = await GetSendGridWebhookList("Meetings", "meetings", cancellationToken).ConfigureAwait(false);
			var rtmsEvents = await GetSendGridWebhookList("RTMS", "rtms", cancellationToken).ConfigureAwait(false);
			var teamChatEvents = await GetSendGridWebhookList("Team Chat", "team-chat", cancellationToken).ConfigureAwait(false);
			var phoneEvents = await GetSendGridWebhookList("Phone", "phone", cancellationToken).ConfigureAwait(false);
			var mailEvents = await GetSendGridWebhookList("Mail", "mail", cancellationToken).ConfigureAwait(false);
			var calendarEvents = await GetSendGridWebhookList("Calendar", "calendar", cancellationToken).ConfigureAwait(false);
			var roomsEvents = await GetSendGridWebhookList("Rooms", "rooms", cancellationToken).ConfigureAwait(false);
			var whiteboardEvents = await GetSendGridWebhookList("Whiteboard", "whiteboard", cancellationToken).ConfigureAwait(false);
			var chatbotEvents = await GetSendGridWebhookList("Chatbot", "chatbot", cancellationToken).ConfigureAwait(false);
			var schedulerEvents = await GetSendGridWebhookList("Scheduler", "scheduler", cancellationToken).ConfigureAwait(false);
			var contactCenterEvents = await GetSendGridWebhookList("Contact Center", "contact-center", cancellationToken).ConfigureAwait(false);
			var eventsEvents = await GetSendGridWebhookList("Events", "events", cancellationToken).ConfigureAwait(false);
			var iqEvents = await GetSendGridWebhookList("Revenue Accelerator", "iq", cancellationToken).ConfigureAwait(false);
			var numberManagementEvents = await GetSendGridWebhookList("Number Management", "number-management", cancellationToken).ConfigureAwait(false);
			var nodeEvents = await GetSendGridWebhookList("Node", "node", cancellationToken).ConfigureAwait(false);
			var qualityManagementEvents = await GetSendGridWebhookList("Quality Management", "quality-management", cancellationToken).ConfigureAwait(false);
			var healthcareEvents = await GetSendGridWebhookList("Healthcare", "healthcare", cancellationToken).ConfigureAwait(false);
			var videoManagementEvents = await GetSendGridWebhookList("Video Management", "video-management", cancellationToken).ConfigureAwait(false);
			var usersEvents = await GetSendGridWebhookList("Users", "users", cancellationToken).ConfigureAwait(false);
			var accountsEvents = await GetSendGridWebhookList("Accounts", "accounts", cancellationToken).ConfigureAwait(false);
			var qssEvents = await GetSendGridWebhookList("Quality of Service Subscription (QSS)", "qss", cancellationToken).ConfigureAwait(false);
			var videoSdkEvents = await GetSendGridWebhookList("Video SDK", "video-sdk", cancellationToken).ConfigureAwait(false);
			var cobrowseSdkEvents = await GetSendGridWebhookList("Cobrowse SDK", "cobrowse-sdk", cancellationToken).ConfigureAwait(false);
			var appsEvents = await GetSendGridWebhookList("Apps", "marketplace", cancellationToken).ConfigureAwait(false);

			var allEvents = meetingEvents
				.Union(rtmsEvents)
				.Union(teamChatEvents)
				.Union(phoneEvents)
				.Union(mailEvents)
				.Union(calendarEvents)
				.Union(roomsEvents)
				.Union(roomsEvents)
				.Union(chatbotEvents)
				.Union(schedulerEvents)
				.Union(contactCenterEvents)
				.Union(eventsEvents)
				.Union(iqEvents)
				.Union(numberManagementEvents)
				.Union(nodeEvents)
				.Union(qualityManagementEvents)
				.Union(healthcareEvents)
				.Union(videoManagementEvents)
				.Union(usersEvents)
				.Union(accountsEvents)
				.Union(qssEvents)
				.Union(videoSdkEvents)
				.Union(cobrowseSdkEvents)
				.Union(appsEvents)
				.GroupBy(
					ev => new { ev.Title, ev.Group },
					ev => new { EventName = ev.Name, IsHandled = eventTypeCSharpSource.Contains(ev.Name), ev.Sample },
					(key, items) => new { Key = key, Events = items.ToArray() })
				.ToArray();

			var issueTitle = "List of Webhook events";
			var issueBody = new StringBuilder();
			issueBody.Append("This issue documents the full list of webhook events in the SendGrid platform and also tracks which ones can be handled by the ZoomNet library. ");

			var resxPath = @"D:\\_build\\ZoomNet\\Source\\ZoomNet.UnitTests\\Properties\\Resource.resx";
			var resxDoc = new XmlDocument();
			resxDoc.Load(resxPath);
			var resxRootNode = resxDoc.DocumentElement.SelectSingleNode("/root");
			var sampleFilesCreated = 0;

			foreach (var grp in allEvents)
			{
				issueBody.AppendLine();
				issueBody.AppendLine("<details>");
				issueBody.AppendLine($"<summary>{grp.Key.Title} ({grp.Events.Count(ev => ev.IsHandled)}/{grp.Events.Length})</summary>");
				issueBody.AppendLine();
				issueBody.AppendLine($"[Documentation](https://developers.zoom.us/docs/api/{grp.Key.Group}/events/)");
				issueBody.AppendLine();
				foreach (var ev in grp.Events)
				{
					var checkState = ev.IsHandled ? "x" : " ";
					issueBody.AppendLine($"- [{checkState}] {ev.EventName}");

					// Add sample to ZoomNet unit testing repository
					var samplePath = $"D:\\_build\\ZoomNet\\Source\\ZoomNet.UnitTests\\WebhookData\\{ev.EventName}.json";
					if (!File.Exists(samplePath))
					{
						var sample = ev.Sample
							.TrimStart('\"')
							.Replace("\\n", "\r\n")
							.Replace("\\t", "\t")
							.Replace("\\\"", "\"")
							.TrimEnd('\"');
						File.WriteAllText(samplePath, sample);
						sampleFilesCreated++;

						var dataNode = resxDoc.CreateNode(XmlNodeType.Element, "data", null);
						var nameAttribute = dataNode.Attributes.Append(resxDoc.CreateAttribute("name"));
						nameAttribute.Value = $"{ev.EventName.Replace('.', '_')}_webhook"; // replace all '.' with '_'
						var typeAttribute = dataNode.Attributes.Append(resxDoc.CreateAttribute("type"));
						typeAttribute.Value = "System.Resources.ResXFileRef, System.Windows.Forms";

						var valueNode = resxDoc.CreateNode(XmlNodeType.Element, "value", null);
						valueNode.InnerText = $@"..\WebhookData\{ev.EventName}.json;System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;utf-8";
						dataNode.AppendChild(valueNode);

						resxRootNode.AppendChild(dataNode);
					}
				}
				issueBody.AppendLine("</details>");
			}

			Console.WriteLine($"{sampleFilesCreated} sample files were created");

			resxDoc.Save(resxPath);

			var grandTotalEvents = allEvents.Sum(g => g.Events.Length);
			var grantTotalHandled = allEvents.Sum(g => g.Events.Count(ev => ev.IsHandled));

			issueBody.AppendLine();
			issueBody.Append($"There is a grand total of {grandTotalEvents} events and ZoomNet can handle {grantTotalHandled} of them.");

			var request = new RepositoryIssueRequest()
			{
				Creator = repoOwner,
				State = ItemStateFilter.Open,
				SortProperty = IssueSort.Created,
				SortDirection = SortDirection.Descending
			};

			var issues = await githubClient.Issue.GetAllForRepository(repoOwner, repoName, request).ConfigureAwait(false);
			var issue = issues.FirstOrDefault(i => i.Title.Equals(issueTitle, StringComparison.OrdinalIgnoreCase));
			if (issue == null)
			{
				var newIssue = new NewIssue(issueTitle)
				{
					Body = issueBody.ToString()
				};
				issue = await githubClient.Issue.Create(repoOwner, repoName, newIssue).ConfigureAwait(false);
				Console.WriteLine($"Issue created: {issue.HtmlUrl}");
			}
			else
			{
				var issueUpdate = issue.ToUpdate();
				issueUpdate.Body = issueBody.ToString();
				issue = await githubClient.Issue.Update(repoOwner, repoName, issue.Number, issueUpdate).ConfigureAwait(false);
				Console.WriteLine($"Issue updated: {issue.HtmlUrl}");
			}
		}

		private static async Task<(string Title, string Group, string Name, string Sample)[]> GetSendGridWebhookList(string title, string group, CancellationToken cancellationToken)
		{
			var url = $"https://developers.zoom.us/api-hub/{group}/events/webhooks.json";
			using HttpClient client = new();

			var httpResponse = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
			if (httpResponse.IsSuccessStatusCode)
			{
				var jsonContent = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
				var jsonRootElement = JsonDocument.Parse(jsonContent).RootElement;

				if (jsonRootElement.TryGetProperty("webhooks", out JsonElement jsonWebhooks))
				{
					var webhooks = jsonWebhooks
						.EnumerateObject()
						.Select(prop => (title, group, prop.Name, prop.Value.GetProperty("post\\requestBody\\content\\application/json\\examples\\json-example\\value", '\\', true).Value.GetRawText()))
						.OrderBy(w => w.Name)
						.ToArray();

					return webhooks;
				}
			}

			return [];
		}
	}
}
