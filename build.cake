
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
var cakeVersion = typeof(ICakeContext).Assembly.GetName().Version.ToString();
var cleanTools = target == "cleantools";

// Reset the target to "Default"
if (cleanTools) target = "Default";


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

Task("Publish")
	.IsDependentOn("Clean")
	.Does(() =>
{
	DotNetPublish($"./{appName}.slnx", new DotNetPublishSettings
	{
		Configuration = configuration,
		PublishSingleFile = true,
		ArgumentCustomization = args => args.Append($"--property:PublishDir={MakeAbsolute(Directory(outputDir))}")
	});
});

Task("Run")
	.IsDependentOn("Publish")
	.Does(() =>
{
	var args = new ProcessArgumentBuilder() .Append("nopause");
	if (cleanTools) args.Append("cleantools");

	Context.ExecuteCommand(new FilePath($"{outputDir}{appName}.exe"), args);
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


///////////////////////////////////////////////////////////////////////////////
// PRIVATE METHODS
///////////////////////////////////////////////////////////////////////////////
static IDisposable GetDisposableVerbosity(this ICakeContext context, Verbosity verbosity)
{
	return verbosity switch
	{
		Verbosity.Diagnostic => context.DiagnosticVerbosity(),
		Verbosity.Minimal => context.MinimalVerbosity(),
		Verbosity.Normal => context.NormalVerbosity(),
		Verbosity.Quiet => context.QuietVerbosity(),
		Verbosity.Verbose => context.VerboseVerbosity(),
		_ => throw new ArgumentOutOfRangeException(nameof(verbosity), $"Unknown verbosity: {verbosity}"),
	}; 
}

static List<string> ExecuteCommand(this ICakeContext context, FilePath exe, string args, bool captureStandardOutput = false, Verbosity verbosity = Verbosity.Diagnostic)
{
	return context.ExecuteCommand(exe, new ProcessArgumentBuilder().Append(args), captureStandardOutput, verbosity);
}

static List<string> ExecuteCommand(this ICakeContext context, FilePath exe, ProcessArgumentBuilder argsBuilder, bool captureStandardOutput = false, Verbosity verbosity = Verbosity.Diagnostic)
{
	using (context.GetDisposableVerbosity(verbosity))
	{
		var processResult = context.StartProcess(
			exe,
			new ProcessSettings()
			{
				Arguments = argsBuilder,
				RedirectStandardOutput = captureStandardOutput,
				RedirectStandardError= true
			},
			out var redirectedOutput,
			out var redirectedError
		);
		
		if (processResult != 0 || redirectedError.Count() > 0)
		{
			var errorMsg = string.Join(Environment.NewLine, redirectedError.Where(s => !string.IsNullOrWhiteSpace(s)));
			var innerException = !string.IsNullOrEmpty(errorMsg) ? new Exception(errorMsg) : null;
			throw new Exception($"{exe} did not complete successfully. Result code: {processResult}", innerException);
		}
		
		return (redirectedOutput ?? Array.Empty<string>()).ToList();
	}
}
