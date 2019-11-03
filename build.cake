
///////////////////////////////////////////////////////////////////////////////
// ARGUMENTS
///////////////////////////////////////////////////////////////////////////////

var target = Argument<string>("target", "Default");
var configuration = Argument<string>("configuration", "Release");


///////////////////////////////////////////////////////////////////////////////
// GLOBAL VARIABLES
///////////////////////////////////////////////////////////////////////////////

var appName = "RefreshResources";

var outputDir = "./artifacts/";
var publishDir = $"{outputDir}Publish/";

var cakeVersion = typeof(ICakeContext).Assembly.GetName().Version.ToString();


///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup(context =>
{
	Information("Building {0} ({1}, {2}) using version {3} of Cake",
		appName,
		configuration,
		target,
		cakeVersion
	);
});

Teardown(context =>
{
	// Executed AFTER the last task.
	Information("Finished running tasks.");
});


///////////////////////////////////////////////////////////////////////////////
// TASK DEFINITIONS
///////////////////////////////////////////////////////////////////////////////

Task("Clean")
	.Does(() =>
{
	// Clean solution directories.
	Information("Cleaning {0}", appName);
	CleanDirectories("./*/bin/" + configuration);
	CleanDirectories("./*/obj/" + configuration);

	// Clean previous artifacts
	Information("Cleaning {0}", outputDir);
	if (DirectoryExists(outputDir)) CleanDirectories(MakeAbsolute(Directory(outputDir)).FullPath);
	else CreateDirectory(outputDir);
});

Task("Restore-NuGet-Packages")
	.IsDependentOn("Clean")
	.Does(() =>
{
	DotNetCoreRestore("./", new DotNetCoreRestoreSettings
	{
		Sources = new [] {
			"https://www.myget.org/F/xunit/api/v3/index.json",
			"https://api.nuget.org/v3/index.json",
		}
	});
});

Task("Build")
	.IsDependentOn("Restore-NuGet-Packages")
	.Does(() =>
{
	DotNetCoreBuild($"./{appName}.sln", new DotNetCoreBuildSettings
	{
		Configuration = configuration,
		NoRestore = true
	});
});


Task("Publish")
	.IsDependentOn("Build")
	.Does(() =>
{
	DotNetCorePublish($"./{appName}.sln", new DotNetCorePublishSettings
	{
		Configuration = configuration,
		NoRestore = true,
		OutputDirectory = publishDir
	});
});


Task("Run")
	.IsDependentOn("Publish")
	.Does(() =>
{
	StartProcess(
		new FilePath($"{publishDir}{appName}.exe"),
		new ProcessSettings()
		{
			Arguments = "nopause"
		});
});


///////////////////////////////////////////////////////////////////////////////
// TARGETS
///////////////////////////////////////////////////////////////////////////////

Task("Default")
	.IsDependentOn("Run");


///////////////////////////////////////////////////////////////////////////////
// EXECUTION
///////////////////////////////////////////////////////////////////////////////

RunTarget(target);
