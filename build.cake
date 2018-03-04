#tool "nuget:?package=NuGetToolsPackager&version=1.1.0"
#tool "nuget:?package=xunit.runner.console&version=2.2.0"

using System.Text.RegularExpressions;

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var nugetApiKey = Argument("nugetApiKey", "");
var trigger = Argument("trigger", "");
var versionSuffix = Argument("versionSuffix", "");

var solutionFileName = "XmlDocMarkdown.sln";
var nugetSource = "https://api.nuget.org/v3/index.json";
var nugetLibraryProjects = new[] { File("src/XmlDocMarkdown.Core/XmlDocMarkdown.Core.csproj").ToString() };
var nugetToolsProjects = new[] { File("src/XmlDocMarkdown/XmlDocMarkdown.csproj").ToString() };

Task("Clean")
	.Does(() =>
	{
		CleanDirectories("src/**/bin");
		CleanDirectories("src/**/obj");
		CleanDirectories("tests/**/bin");
		CleanDirectories("tests/**/obj");
		CleanDirectories("release");
	});

Task("Build")
	.Does(() =>
	{
		DotNetCoreRestore(solutionFileName);
		DotNetCoreBuild(solutionFileName, new DotNetCoreBuildSettings { Configuration = configuration, ArgumentCustomization = args => args.Append("--verbosity normal") });
	});

Task("Rebuild")
	.IsDependentOn("Clean")
	.IsDependentOn("Build");

Task("GenerateDocs")
	.IsDependentOn("Build")
	.Does(() => GenerateDocs(verify: false));

Task("VerifyGenerateDocs")
	.IsDependentOn("Build")
	.Does(() => GenerateDocs(verify: true));

Task("Test")
	.IsDependentOn("VerifyGenerateDocs")
	.Does(() =>
	{
		foreach (var projectPath in GetFiles("tests/**/*.Tests.csproj").Select(x => x.FullPath))
			DotNetCoreTest(projectPath, new DotNetCoreTestSettings { Configuration = configuration });
	});

Task("NuGetPackage")
	.IsDependentOn("Rebuild")
	.IsDependentOn("Test")
	.Does(() =>
	{
		if (string.IsNullOrEmpty(versionSuffix) && !string.IsNullOrEmpty(trigger))
			versionSuffix = Regex.Match(trigger, @"^v[^\.]+\.[^\.]+\.[^\.]+-(.+)").Groups[1].ToString();

		foreach (var nugetLibraryProject in nugetLibraryProjects)
			DotNetCorePack(nugetLibraryProject, new DotNetCorePackSettings { Configuration = configuration, OutputDirectory = "release", VersionSuffix = versionSuffix });

		foreach (string nugetToolsProject in nugetToolsProjects)
		{
			ExecuteProcess(Context.Tools.Resolve("NuGetToolsPackager.exe").ToString(), $@"{nugetToolsProject} --platform net461" +
				(string.IsNullOrEmpty(versionSuffix) ? "" : $@" --versionSuffix ""{versionSuffix}"""));

			NuGetPack(System.IO.Path.ChangeExtension(nugetToolsProject, ".nuspec"), new NuGetPackSettings { OutputDirectory = "release" });
		}
	});

Task("NuGetPublish")
	.IsDependentOn("NuGetPackage")
	.Does(() =>
	{
		var nupkgPaths = GetFiles("release/*.nupkg").Select(x => x.FullPath).ToList();

		string version = null;
		foreach (var nupkgPath in nupkgPaths)
		{
			string nupkgVersion = Regex.Match(nupkgPath, @"\.([^\.]+\.[^\.]+\.[^\.]+)\.nupkg$").Groups[1].ToString();
			if (version == null)
				version = nupkgVersion;
			else if (version != nupkgVersion)
				throw new InvalidOperationException($"Mismatched package versions '{version}' and '{nupkgVersion}'.");
		}

		if (!string.IsNullOrEmpty(nugetApiKey) && (trigger == null || Regex.IsMatch(trigger, "^v[0-9]")))
		{
			if (trigger != null && trigger != $"v{version}")
				throw new InvalidOperationException($"Trigger '{trigger}' doesn't match package version '{version}'.");

			var pushSettings = new NuGetPushSettings { ApiKey = nugetApiKey, Source = nugetSource };
			foreach (var nupkgPath in nupkgPaths)
				NuGetPush(nupkgPath, pushSettings);
		}
		else
		{
			Information("To publish NuGet packages, push this git tag: v" + version);
		}
	});

Task("Default")
	.IsDependentOn("Build");

void GenerateDocs(bool verify)
{
	GenerateDocs(verify, File($"src/XmlDocMarkdown.Core/bin/{configuration}/net461/XmlDocMarkdown.Core.dll").ToString(), "../src/XmlDocMarkdown.Core");
	GenerateDocs(verify, File($"tests/ExampleAssembly/bin/{configuration}/netstandard1.1/ExampleAssembly.dll").ToString(), "../tests/ExampleAssembly");
}

void GenerateDocs(bool verify, string docsAssembly, string docsSourceUri)
{
	string exePath = File($"src/XmlDocMarkdown/bin/{configuration}/net461/XmlDocMarkdown.exe").ToString();
	string arguments = $@"{docsAssembly} docs --source ""{docsSourceUri}"" --newline lf --clean" + (verify ? " --verify" : "");
	if (Context.Environment.Platform.IsUnix())
	{
		arguments = exePath + " " + arguments;
		exePath = "mono";
	}
	int exitCode = StartProcess(exePath, arguments);
	if (exitCode == 1 && verify)
		throw new InvalidOperationException("Generated docs don't match; use --target=GenerateDocs to regenerate.");
	else if (exitCode != 0)
		throw new InvalidOperationException($"Docs generation failed with exit code {exitCode}.");
}

void ExecuteProcess(string exePath, string arguments)
{
	if (Context.Environment.Platform.IsUnix())
	{
		arguments = exePath + " " + arguments;
		exePath = "mono";
	}
	int exitCode = StartProcess(exePath, arguments);
	if (exitCode != 0)
		throw new InvalidOperationException($"{exePath} failed with exit code {exitCode}.");
}

RunTarget(target);
