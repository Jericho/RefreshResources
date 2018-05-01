using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Octokit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace RefreshResources
{
    class Program
    {
        private const string ROOT_FOLDER = "E:\\_build\\";
        private const string SOURCE_FOLDER = ROOT_FOLDER + "resources";

        private static string[] PROJECTS = new string[]
        {
            "CakeMail.RestClient",
            "Picton",
            "Picton.Messaging",
            "StrongGrid"
        };

        private static IDictionary<string, string> LABELS = new Dictionary<string, string>
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

        private static string GITHUB_USERNAME = Environment.GetEnvironmentVariable("GITHUB_USERNAME");
        private static string GITHUB_PASSWORD = Environment.GetEnvironmentVariable("GITHUB_PASSWORD");

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

            // Flush the console key buffer
            while (Console.KeyAvailable) Console.ReadKey(true);

            // Wait for user to press a key
            Console.WriteLine("\r\nPress any key to exit...");
            Console.ReadKey();
        }

        private static async Task RefreshGithubLabels()
        {
            var credentials = new Octokit.Credentials(GITHUB_USERNAME, GITHUB_PASSWORD);
            var githubClient = new GitHubClient(new ProductHeaderValue("RefreshResources")) { Credentials = credentials };

            foreach (var project in PROJECTS)
            {
                await RefreshGithubLabels(githubClient, project).ConfigureAwait(false);
            }
        }

        private static async Task RefreshGithubLabels(IGitHubClient githubClient, string projectName)
        {
            var labels = await githubClient.Issue.Labels.GetAllForRepository("jericho", projectName).ConfigureAwait(false);

            foreach (var label in LABELS)
            {
                // Perform case-insensitive search
                var existingLabel = labels.FirstOrDefault(l => l.Name.Equals(label.Key, StringComparison.OrdinalIgnoreCase));

                // Create label if it doesn't already exist
                if (existingLabel == null)
                {
                    await githubClient.Issue.Labels.Create("jericho", projectName, new NewLabel(label.Key, label.Value)).ConfigureAwait(false);
                }

                // Update the existing label if it doesn't match perfectly (wrong color or inconstent casing)
                else if (!existingLabel.Name.Equals(label.Key, StringComparison.Ordinal) || (!existingLabel.Color.Equals(label.Value, StringComparison.Ordinal)))
                {
                    await githubClient.Issue.Labels.Update("jericho", projectName, existingLabel.Name, new LabelUpdate(label.Key, label.Value)).ConfigureAwait(false);
                }
            }
        }

        private static async Task RefreshResourcesAsync()
        {
            var repo = new LibGit2Sharp.Repository(SOURCE_FOLDER);
            var author = repo.Config.BuildSignature(DateTimeOffset.Now);
            var httpClient = new HttpClient();

            // STEP 1 - Git pull in case there are some changes in the GitHub repo that have not been pulled (this would be very surprising, but better safe than sorry)
            var pullOptions = new PullOptions()
            {
                FetchOptions = new FetchOptions()
            };
            Commands.Pull(repo, author, pullOptions);

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

            // STEP 3 - Refresh the Cake bootstrap
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://raw.githubusercontent.com/cake-build/resources/develop/build.ps1"))
            {
                var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                content = content
                    .Trim('\n')
                    .Replace("\n", "\r\n");

                File.WriteAllText(Path.Combine(SOURCE_FOLDER, "build.ps1"), content);
            }

            // STEP 4 - Commit the changes (if any)
            var changes = repo.Diff.Compare<TreeChanges>();
            if (changes.Any())
            {
                Commands.Stage(repo, changes.Select(c => c.Path));
                var commit = repo.Commit("Refresh resources", author, author);

                var pushOptions = new PushOptions()
                {
                    CredentialsProvider = new CredentialsHandler(
                    (url, usernameFromUrl, types) =>
                        new UsernamePasswordCredentials()
                        {
                            Username = GITHUB_USERNAME,
                            Password = GITHUB_PASSWORD
                        })
                };
                repo.Network.Push(repo.Branches["master"], pushOptions);
            }

            Console.WriteLine();
            Console.WriteLine("***** Resources *****");
            var modifiedFiles = changes.Added
                .Union(changes.Modified);
            if (modifiedFiles.Any())
            {
                Console.WriteLine(string.Join("\r\n", modifiedFiles.Select(c => "    " + c.Path)));
            }
            else
            {
                Console.WriteLine("All files already up to date");
            }
        }

        private static async Task CopyResourceFiles()
        {
            var files = GetSourceFiles(SOURCE_FOLDER);
            foreach (var project in PROJECTS)
            {
                await CopyResourceFilesToProject(files, project).ConfigureAwait(false);
            }
        }

        private static async Task CopyResourceFilesToProject(IEnumerable<FileInfo> resoureFiles, string projectName)
        {
            if (string.IsNullOrEmpty(projectName))
            {
                throw new ArgumentException("You must specify the name of the project", nameof(projectName));
            }

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

            Console.WriteLine();
            Console.WriteLine($"***** {projectName} *****");
            if (modifiedFiles.Any())
            {
                Console.WriteLine(string.Join("\r\n", modifiedFiles.Select(f => "    " + f)));
            }
            else
            {
                Console.WriteLine("All files already up to date");
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
    }
}
