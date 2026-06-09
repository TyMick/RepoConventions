using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace RepoConventions.Tests;

internal sealed class ConventionExecutionTests
{
	[Test]
	public async Task CommitModeFailsWhenConfigIsMissing()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(result.StandardError, Does.Contain(".github/conventions.yml"));
		}
	}

	[Test]
	public async Task CommitModeFailsWhenCustomConfigIsMissing()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();

		var result = await CliInvocation.InvokeAsync(["apply", "--config", ".config/repo-conventions.yml"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(result.StandardError, Does.Contain(".config/repo-conventions.yml"));
		}
	}

	[Test]
	public async Task CommitModeFailsWithFriendlyMessageWhenConfigYamlIsInvalid()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", "\tconventions:\n\t- path: ./conventions/add-file\n");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(result.StandardError, Does.Contain("Configuration file"));
			Assert.That(result.StandardError, Does.Contain(".github/conventions.yml"));
			Assert.That(result.StandardError, Does.Contain("is not valid YAML:"));
			Assert.That(result.StandardError, Does.Not.Contain("Unhandled exception"));
		}
	}

	[Test]
	public async Task CommitModeDoesNotSwallowUnexpectedInvalidOperationException()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		var summaryDirectory = TemporaryDirectoryPath.Create();
		Directory.CreateDirectory(summaryDirectory);
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: local-test/remote-conventions/conventions/add-file@main
			""");
		await repo.CommitAllAsync("Initial commit.");

		try
		{
			var standardOutput = new StringWriter();
			var standardError = new StringWriter();
			var summaryPath = Path.Combine(summaryDirectory, "step-summary.md");
			await File.WriteAllTextAsync(summaryPath, "Existing summary." + Environment.NewLine);

			var exitCode = await RepoConventionsCli.InvokeAsync(
				["apply"],
				repo.RootPath,
				standardOutput,
				standardError,
				_ => throw new InvalidOperationException("Unexpected resolver failure."),
				externalCommandRunner: null,
				useGitHubActionsGroupMarkers: null,
				gitHubStepSummaryPath: summaryPath,
				CancellationToken.None);
			var stepSummary = await File.ReadAllTextAsync(summaryPath);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(exitCode, Is.Not.Zero);
				Assert.That(standardError.ToString(), Does.Contain("Unhandled exception"));
				Assert.That(standardError.ToString(), Does.Contain("Unexpected resolver failure."));
				Assert.That(stepSummary, Does.StartWith("Existing summary." + Environment.NewLine + "Unhandled exception:" + Environment.NewLine + "```text" + Environment.NewLine));
				Assert.That(stepSummary, Does.Contain("System.InvalidOperationException: Unexpected resolver failure."));
				Assert.That(stepSummary, Does.EndWith("```" + Environment.NewLine));
			}
		}
		finally
		{
			Directory.Delete(summaryDirectory, recursive: true);
		}
	}

	[Test]
	public async Task CommitModeReturnsCanceledExitCodeWhenCancellationIsRequested()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath, cancellationToken: cancellation.Token);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.EqualTo(RepoConventionsCli.CanceledExitCode));
			Assert.That(result.StandardError, Is.EqualTo(RepoConventionsCli.ShutdownRequestedMessage + Environment.NewLine));
			Assert.That(result.StandardError, Does.Not.Contain("Unhandled exception"));
		}
	}

	[Test]
	public async Task CommitModeStopsConventionScriptWhenCancellationIsRequested()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/slow
			""");
		repo.WriteFile(".github/conventions/slow/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'started.txt') -Value 'started'
			Start-Sleep -Seconds 1
			Set-Content -Path (Join-Path $PWD 'completed.txt') -Value 'completed'
			""");
		await repo.CommitAllAsync("Initial commit.");
		using var cancellation = new CancellationTokenSource();

		var invocationTask = CliInvocation.InvokeAsync(["apply"], repo.RootPath, cancellationToken: cancellation.Token);
		try
		{
			await WaitForFileAsync(repo, "started.txt");
			await cancellation.CancelAsync();

			var result = await invocationTask;
			await Task.Delay(TimeSpan.FromMilliseconds(1500));

			using (Assert.EnterMultipleScope())
			{
				Assert.That(result.ExitCode, Is.EqualTo(RepoConventionsCli.CanceledExitCode));
				Assert.That(repo.FileExists("completed.txt"), Is.False);
				Assert.That(result.StandardError, Is.EqualTo(RepoConventionsCli.ShutdownRequestedMessage + Environment.NewLine));
			}
		}
		finally
		{
			await cancellation.CancelAsync();
			await invocationTask;
		}
	}

	[Test]
	public async Task CommitModeAppliesExecutableConventionAndCreatesCommit()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/add-file
			""");
		repo.WriteFile(".github/conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'created.txt') -Value 'created'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);
		var normalizedOutput = NormalizeConventionOutput(result.StandardOutput);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("created.txt"), Is.True);
			Assert.That(result.StandardOutput, Does.StartWith("Applying 1 conventions..." + Environment.NewLine));
			Assert.That(normalizedOutput, Does.Contain("\nConvention add-file\nCreated 1 commit for convention add-file."));
			Assert.That(result.StandardOutput, Does.Contain("Created 1 commit for convention add-file."));
			Assert.That(result.StandardOutput, Does.EndWith("Created 1 commit total." + Environment.NewLine));
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Apply convention add-file"));
		}
	}

	[Test]
	public async Task CommitModeUsesReferenceCommitMessageForAutomaticCommit()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/add-file
			  commit:
			    message: Update generated file
			""");
		repo.WriteFile(".github/conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'created.txt') -Value 'created'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("created.txt"), Is.True);
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Update generated file"));
		}
	}

	[Test]
	public async Task CommitModeUsesConventionCommitMessageForAutomaticCommit()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/add-file
			""");
		repo.WriteFile(".github/conventions/add-file/convention.yml", """
			commit:
			  message: Update convention output
			""");
		repo.WriteFile(".github/conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'created.txt') -Value 'created'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("created.txt"), Is.True);
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Update convention output"));
		}
	}

	[Test]
	public async Task CommitModeReferenceCommitMessageOverridesConventionCommitMessage()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/add-file
			  commit:
			    message: Update from reference
			""");
		repo.WriteFile(".github/conventions/add-file/convention.yml", """
			commit:
			  message: Update from convention
			""");
		repo.WriteFile(".github/conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'created.txt') -Value 'created'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("created.txt"), Is.True);
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Update from reference"));
		}
	}

	[Test]
	public async Task CommitModePassesCommitMessageToChildConvention()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/parent
			  commit:
			    message: Update composite files
			""");
		repo.WriteFile(".github/conventions/parent/convention.yml", """
			conventions:
			- path: ../child
			""");
		repo.WriteFile(".github/conventions/child/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'child.txt') -Value 'child'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("child.txt"), Is.True);
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Update composite files"));
		}
	}

	[Test]
	public async Task CommitModeChildCommitMessageOverridesInheritedCommitMessage()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/parent
			  commit:
			    message: Update composite files
			""");
		repo.WriteFile(".github/conventions/parent/convention.yml", """
			conventions:
			- path: ../child
			  commit:
			    message: Update child files
			""");
		repo.WriteFile(".github/conventions/child/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'child.txt') -Value 'child'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("child.txt"), Is.True);
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Update child files"));
		}
	}

	[Test]
	public async Task CommitModeAmendsConsecutiveAutomaticCommitWithSameMessage()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/first
			  commit:
			    message: Update shared files
			- path: ./conventions/second
			  commit:
			    message: Update shared files
			""");
		repo.WriteFile(".github/conventions/first/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'first.txt') -Value 'first'
			""");
		repo.WriteFile(".github/conventions/second/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'second.txt') -Value 'second'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);
		var normalizedOutput = NormalizeConventionOutput(result.StandardOutput);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("first.txt"), Is.True);
			Assert.That(repo.FileExists("second.txt"), Is.True);
			Assert.That(normalizedOutput, Does.Contain("Updated previous commit for convention second."));
			Assert.That(result.StandardOutput, Does.EndWith("Created 1 commit total." + Environment.NewLine));
			Assert.That(await repo.GetRecentCommitMessagesAsync(3), Is.EqualTo(s_amendedSharedCommitMessages));
		}
	}

	[TestCase("\"\"")]
	[TestCase("\"   \"")]
	public async Task CommitModeTreatsBlankReferenceCommitMessageAsUnspecified(string yamlMessage)
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", $$"""
			conventions:
			- path: ./conventions/add-file
			  commit:
			    message: {{yamlMessage}}
			""");
		repo.WriteFile(".github/conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'created.txt') -Value 'created'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("created.txt"), Is.True);
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Apply convention add-file"));
		}
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task CommitModeGitNoVerifyControlsPreCommitHookBypass(bool gitNoVerify)
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/add-file
			""");
		repo.WriteFile(".github/conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'created.txt') -Value 'created'
			""");
		await repo.CommitAllAsync("Initial commit.");
		repo.InstallFailingHook("pre-commit");

		var arguments = gitNoVerify ? new[] { "apply", "--git-no-verify" } : ["apply"];
		var result = await CliInvocation.InvokeAsync(arguments, repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.EqualTo(gitNoVerify ? 0 : 1));
			Assert.That(result.StandardError, gitNoVerify ? Is.Empty : Does.Contain("git commit -m Apply convention add-file failed:"));
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo(gitNoVerify ? "Apply convention add-file" : "Initial commit."));
			Assert.That(repo.FileExists("created.txt"), Is.True);
			Assert.That(await repo.GetWorkingTreeStatusAsync(), Is.EqualTo(gitNoVerify ? "" : "A  created.txt"));
		}
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task CommitModePassesGitNoVerifyToExecutableConventionScript(bool gitNoVerify)
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/self-commit
			""");
		repo.WriteFile(".github/conventions/self-commit/convention.ps1", """
			param([string] $configPath)
			$config = Get-Content -Raw $configPath | ConvertFrom-Json
			Set-Content -Path (Join-Path $PWD 'script-created.txt') -Value 'created'
			git add script-created.txt
			$arguments = @('commit', '-m', 'Script-created commit.')
			if ($config.gitNoVerify) { $arguments += '--no-verify' }
			git @arguments
			if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
			""");
		await repo.CommitAllAsync("Initial commit.");
		repo.InstallFailingHook("commit-msg");

		var arguments = gitNoVerify ? new[] { "apply", "--git-no-verify" } : ["apply"];
		var result = await CliInvocation.InvokeAsync(arguments, repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.EqualTo(gitNoVerify ? 0 : 1));
			Assert.That(result.StandardError, gitNoVerify ? Is.Empty : Does.Contain("Convention self-commit failed."));
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo(gitNoVerify ? "Script-created commit." : "Initial commit."));
			Assert.That(repo.FileExists("script-created.txt"), Is.EqualTo(gitNoVerify));
		}
	}

	[Test]
	public async Task CommitModeReportsTotalCommitsCreatedByConventionScript()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/self-commit
			""");
		repo.WriteFile(".github/conventions/self-commit/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'first.txt') -Value 'first'
			git add first.txt
			git commit -m 'First self-created commit.'
			Set-Content -Path (Join-Path $PWD 'second.txt') -Value 'second'
			git add second.txt
			git commit -m 'Second self-created commit.'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("first.txt"), Is.True);
			Assert.That(repo.FileExists("second.txt"), Is.True);
			Assert.That(result.StandardOutput, Does.Contain("Created 2 commits for convention self-commit."));
			Assert.That(await repo.GetRecentCommitMessagesAsync(2), Is.EqualTo(s_selfCreatedCommitMessages));
		}
	}

	[Test]
	public async Task CommitModeWritesFinalSummaryLineToGitHubStepSummary()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		var summaryDirectory = TemporaryDirectoryPath.Create();
		Directory.CreateDirectory(summaryDirectory);
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/add-file
			""");
		repo.WriteFile(".github/conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'created.txt') -Value 'created'
			""");
		await repo.CommitAllAsync("Initial commit.");

		try
		{
			var summaryPath = Path.Combine(summaryDirectory, "step-summary.md");
			await File.WriteAllTextAsync(summaryPath, "Existing summary." + Environment.NewLine);

			var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath, gitHubStepSummaryPath: summaryPath);
			var stepSummary = await File.ReadAllTextAsync(summaryPath);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(result.ExitCode, Is.Zero);
				Assert.That(stepSummary, Is.EqualTo("Existing summary." + Environment.NewLine + "Created 1 commit total." + Environment.NewLine));
				Assert.That(stepSummary, Does.Not.Contain("Created 1 commit for convention add-file."));
			}
		}
		finally
		{
			Directory.Delete(summaryDirectory, recursive: true);
		}
	}

	[Test]
	public async Task CommitModeReportsNoChangesForConventionThatCreatesNoCommit()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/no-op
			""");
		repo.WriteFile(".github/conventions/no-op/convention.ps1", """
			param([string] $configPath)
			Write-Output 'script output'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);
		var normalizedOutput = NormalizeConventionOutput(result.StandardOutput);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(normalizedOutput, Does.Contain("\nConvention no-op\nscript output\n"));
			Assert.That(normalizedOutput, Does.Contain("No changes for convention no-op."));
			Assert.That(normalizedOutput, Does.EndWith("No commits created.\n"));
			Assert.That(normalizedOutput, Does.Not.Contain("Created 1 commit for convention no-op."));
		}
	}

	[Test]
	public async Task CommitModeDecodesUtf8ConventionOutput()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/utf8-output
			""");
		repo.WriteFile(".github/conventions/utf8-output/convention.ps1", """
			[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
			Write-Output "$([char] 0x25CF) 23 files found"
			[Console]::Error.WriteLine("$([char] 0x25E6) `"global.json`"")
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(result.StandardOutput, Does.Contain("\u25CF 23 files found"));
			Assert.That(result.StandardError, Does.Contain("\u25E6 \"global.json\""));
		}
	}

	[Test]
	public async Task CommitModeWritesBlankLineBeforeStartMessageWithoutGitHubActionsGroupMarkers()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/add-file
			""");
		repo.WriteFile(".github/conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Write-Output 'script output'
			Set-Content -Path (Join-Path $PWD 'created.txt') -Value 'created'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath, useGitHubActionsGroupMarkers: false);
		var normalizedOutput = result.StandardOutput.ReplaceLineEndings("\n");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(normalizedOutput, Does.StartWith("Applying 1 conventions...\n\nConvention add-file\nscript output\nCreated 1 commit for convention add-file."));
			Assert.That(normalizedOutput, Does.Not.Contain("::group::"));
			Assert.That(normalizedOutput, Does.Not.Contain("::endgroup::"));
		}
	}

	[Test]
	public async Task CommitModeWrapsConventionOutputInGitHubActionsGroupMarkers()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/add-file
			""");
		repo.WriteFile(".github/conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Write-Output 'script output'
			Set-Content -Path (Join-Path $PWD 'created.txt') -Value 'created'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath, useGitHubActionsGroupMarkers: true);
		var normalizedOutput = result.StandardOutput.ReplaceLineEndings("\n");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(normalizedOutput, Does.StartWith("Applying 1 conventions...\n::group::Convention add-file\nscript output\n::endgroup::\nCreated 1 commit for convention add-file."));
			Assert.That(normalizedOutput, Does.Not.Contain("\nConvention add-file\nscript output"));
		}
	}

	[Test]
	public async Task CommitModeWritesNoChangeMessageInsideGitHubActionsGroupMarkers()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/no-op
			""");
		repo.WriteFile(".github/conventions/no-op/convention.ps1", """
			param([string] $configPath)
			Write-Output 'script output'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath, useGitHubActionsGroupMarkers: true);
		var normalizedOutput = result.StandardOutput.ReplaceLineEndings("\n");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(normalizedOutput, Does.StartWith("Applying 1 conventions...\n::group::Convention no-op\nscript output\nNo changes for convention no-op.\n::endgroup::\nNo commits created."));
			Assert.That(normalizedOutput, Does.Not.Contain("::endgroup::\nNo changes for convention no-op."));
		}
	}

	[Test]
	public async Task CommitModePassesOnlySettingsToExecutableConvention()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/write-settings
			  settings:
			    greeting: hello
			    enabled: true
			""");
		repo.WriteFile(".github/conventions/write-settings/convention.ps1", """
			param([string] $configPath)
			$config = Get-Content -Raw $configPath | ConvertFrom-Json
			$output = @{ hasSettings = ($null -ne $config.settings); propertyCount = ($config.PSObject.Properties | Measure-Object).Count; greeting = $config.settings.greeting; enabled = $config.settings.enabled }
			$output | ConvertTo-Json -Compress | Set-Content -Path (Join-Path $PWD 'settings.json')
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(await repo.ReadFileAsync("settings.json"), Does.Contain("\"hasSettings\":true"));
			Assert.That(await repo.ReadFileAsync("settings.json"), Does.Contain("\"propertyCount\":1"));
			Assert.That(await repo.ReadFileAsync("settings.json"), Does.Contain("\"greeting\":\"hello\""));
		}
	}

	[Test]
	public async Task CommitModeSupportsRepositoryRootRelativeConventionPaths()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: /conventions/root-relative
			""");
		repo.WriteFile("conventions/root-relative/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'root-relative.txt') -Value 'created'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("root-relative.txt"), Is.True);
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Apply convention root-relative"));
		}
	}

	[Test]
	public async Task CommitModeSupportsRepoOptionOutsideRepositoryRoot()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		var launchDirectory = TemporaryDirectoryPath.Create();
		Directory.CreateDirectory(launchDirectory);
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/add-file
			""");
		repo.WriteFile(".github/conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'created.txt') -Value 'created'
			""");
		await repo.CommitAllAsync("Initial commit.");

		try
		{
			var result = await CliInvocation.InvokeAsync(["apply", "--repo", repo.RootPath], launchDirectory);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(result.ExitCode, Is.Zero);
				Assert.That(repo.FileExists("created.txt"), Is.True);
				Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Apply convention add-file"));
			}
		}
		finally
		{
			DeleteDirectoryWithRetries(launchDirectory);
		}
	}

	[Test]
	public async Task CommitModeSupportsCustomConfigPath()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".config/repo-conventions.yml", """
			conventions:
			- path: /conventions/add-file
			""");
		repo.WriteFile("conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'custom-config.txt') -Value 'created'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply", "--config", ".config/repo-conventions.yml"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("custom-config.txt"), Is.True);
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Apply convention add-file"));
		}
	}

	[Test]
	public async Task CommitModeResolvesCustomConfigPathFromCurrentDirectory()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		var launchDirectory = TemporaryDirectoryPath.Create();
		Directory.CreateDirectory(launchDirectory);
		Directory.CreateDirectory(Path.Combine(launchDirectory, ".config"));
		await File.WriteAllTextAsync(Path.Combine(launchDirectory, ".config", "repo-conventions.yml"), """
			conventions:
			- path: /conventions/add-file
			""");
		repo.WriteFile("conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'custom-config-from-cwd.txt') -Value 'created'
			""");
		await repo.CommitAllAsync("Initial commit.");

		try
		{
			var result = await CliInvocation.InvokeAsync(["apply", "--repo", repo.RootPath, "--config", ".config/repo-conventions.yml"], launchDirectory);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(result.ExitCode, Is.Zero);
				Assert.That(repo.FileExists("custom-config-from-cwd.txt"), Is.True);
				Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Apply convention add-file"));
			}
		}
		finally
		{
			DeleteDirectoryWithRetries(launchDirectory);
		}
	}

	[Test]
	public async Task CommitModeUsesConfiguredTempRootFromCurrentDirectoryForRemoteCloneAndScriptInputFile()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		using var remoteRepo = await TemporaryGitRepository.CreateAsync();
		var launchDirectory = TemporaryDirectoryPath.Create();
		Directory.CreateDirectory(launchDirectory);
		remoteRepo.WriteFile("conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'temp-input-path.txt') -Value $configPath
			Set-Content -Path (Join-Path $PWD 'created.txt') -Value 'created'
			""");
		await remoteRepo.CommitAllAsync("Initial remote commit.");

		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: local-test/remote-conventions/conventions/add-file@main
			""");
		await repo.CommitAllAsync("Initial commit.");

		try
		{
			var tempRoot = Path.Combine(launchDirectory, ".artifacts", "repo-conventions-temp");
			var result = await CliInvocation.InvokeAsync(
				["apply", "--repo", repo.RootPath, "--temp", ".artifacts/repo-conventions-temp"],
				launchDirectory,
				LocalTestRemoteRepositoryUrlResolver(remoteRepo));

			using (Assert.EnterMultipleScope())
			{
				Assert.That(result.ExitCode, Is.Zero);
				Assert.That(repo.FileExists("created.txt"), Is.True);
				Assert.That(repo.FileExists("temp-input-path.txt"), Is.True);
				var normalizedInputPath = (await repo.ReadFileAsync("temp-input-path.txt")).Trim().Replace('\\', '/');
				var normalizedTempRoot = tempRoot.Replace('\\', '/');
				Assert.That(normalizedInputPath, Does.StartWith(normalizedTempRoot + "/"));
				Assert.That(Directory.Exists(tempRoot), Is.True);
				Assert.That(Directory.EnumerateDirectories(tempRoot).Any(), Is.True);
			}
		}
		finally
		{
			DeleteDirectoryWithRetries(launchDirectory);
		}
	}

	[Test]
	public async Task CommitModeFailsWhenTempRootCannotBeCreated()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/add-file
			""");
		repo.WriteFile(".github/conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'created.txt') -Value 'created'
			""");
		repo.WriteFile(".artifacts/blocked-temp-root", "blocked");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply", "--temp", ".artifacts/blocked-temp-root"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(result.StandardError, Does.Contain("blocked-temp-root"));
			Assert.That(repo.FileExists("created.txt"), Is.False);
		}
	}

	[Test]
	public async Task CommitModeFailsWithMissingConventionDirectoryMessage()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/use-slnx
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(result.StandardError, Does.Contain("directory"));
			Assert.That(result.StandardError, Does.Contain("./conventions/use-slnx"));
			Assert.That(result.StandardError, Does.Not.Contain("did not contain convention.yml or convention.ps1"));
		}
	}

	[Test]
	public async Task CommitModeFailsWhenNonExecutableConventionYamlOmitsChildConventions()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/metadata-only
			""");
		repo.WriteFile(".github/conventions/metadata-only/convention.yml", """
			pull-request:
			  labels:
			    - maintenance
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(result.StandardError, Does.Contain("must contain a 'conventions' sequence"));
		}
	}

	[Test]
	public async Task CommitModeAppliesCompositeConventionBeforeItsScript()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/parent
			""");
		repo.WriteFile(".github/conventions/parent/convention.yml", """
			conventions:
			- path: ../child
			""");
		repo.WriteFile(".github/conventions/child/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'child.txt') -Value 'child'
			""");
		repo.WriteFile(".github/conventions/parent/convention.ps1", """
			param([string] $configPath)
			if (-not (Test-Path (Join-Path $PWD 'child.txt'))) { throw 'child missing' }
			Set-Content -Path (Join-Path $PWD 'parent.txt') -Value 'parent'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);
		var normalizedOutput = NormalizeConventionOutput(result.StandardOutput);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("child.txt"), Is.True);
			Assert.That(repo.FileExists("parent.txt"), Is.True);
			Assert.That(normalizedOutput, Does.Contain("\nConvention child < parent\nCreated 1 commit for convention child.\n\nConvention parent\n"));
			Assert.That(result.StandardOutput, Does.Contain("Created 1 commit for convention parent."));
			Assert.That(await repo.GetRecentCommitMessagesAsync(2), Is.EqualTo(s_parentThenChildCommitMessages));
		}
	}

	[Test]
	public async Task CommitModeDoesNotDoubleCountChildCommitForCompositeConvention()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/parent
			""");
		repo.WriteFile(".github/conventions/parent/convention.yml", """
			conventions:
			- path: ../child
			""");
		repo.WriteFile(".github/conventions/child/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'child.txt') -Value 'child'
			""");
		repo.WriteFile(".github/conventions/parent/convention.ps1", """
			param([string] $configPath)
			if (-not (Test-Path (Join-Path $PWD 'child.txt'))) { throw 'child missing' }
			Write-Output 'parent saw child'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);
		var normalizedOutput = NormalizeConventionOutput(result.StandardOutput);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("child.txt"), Is.True);
			Assert.That(normalizedOutput, Does.Contain("\nConvention child < parent\nCreated 1 commit for convention child.\n\nConvention parent\nparent saw child\n"));
			Assert.That(normalizedOutput, Does.Contain("No changes for convention parent."));
			Assert.That(normalizedOutput, Does.Not.Contain("Created 1 commit for convention parent."));
			Assert.That(normalizedOutput, Does.Not.Contain("Created 2 commits for convention parent."));
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Apply convention child"));
		}
	}

	[Test]
	public async Task CommitModeShowsFullAncestorChainForNestedCompositeConvention()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/grandparent
			""");
		repo.WriteFile(".github/conventions/grandparent/convention.yml", """
			conventions:
			- path: ../parent
			""");
		repo.WriteFile(".github/conventions/parent/convention.yml", """
			conventions:
			- path: ../child
			""");
		repo.WriteFile(".github/conventions/child/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'child.txt') -Value 'child'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);
		var normalizedOutput = NormalizeConventionOutput(result.StandardOutput);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("child.txt"), Is.True);
			Assert.That(normalizedOutput, Does.Contain("\nConvention child < parent < grandparent\nCreated 1 commit for convention child.\n\nConvention parent < grandparent\n"));
			Assert.That(normalizedOutput, Does.Contain("\nConvention grandparent\nNo changes for convention grandparent."));
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Apply convention child"));
		}
	}

	[Test]
	public async Task CommitModePropagatesExactTypedSettingsFromCompositeConvention()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/parent
			  settings:
			    name: repo-conventions
			    version: 10
			    enabled: true
			    labels:
			      - automation
			      - conventions
			    metadata:
			      owner: Faithlife
			    nullable: null
			""");
		repo.WriteFile(".github/conventions/parent/convention.yml", """
			conventions:
			- path: ../write-settings
			  settings:
			    stringValue: ${{ settings.name }}
			    numberValue: ${{ settings.version }}
			    boolValue: ${{ settings.enabled }}
			    arrayValue: ${{ settings.labels }}
			    objectValue: ${{ settings.metadata }}
			    nullValue: ${{ settings.nullable }}
			""");
		repo.WriteFile(".github/conventions/write-settings/convention.ps1", """
			param([string] $configPath)
			$config = Get-Content -Raw $configPath | ConvertFrom-Json
			$config.settings | ConvertTo-Json -Compress -Depth 10 | Set-Content -Path (Join-Path $PWD 'settings.json')
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);
		var settingsJson = await repo.ReadFileAsync("settings.json");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(settingsJson, Does.Contain("\"stringValue\":\"repo-conventions\""));
			Assert.That(settingsJson, Does.Contain("\"numberValue\":10"));
			Assert.That(settingsJson, Does.Contain("\"boolValue\":true"));
			Assert.That(settingsJson, Does.Contain("\"arrayValue\":[\"automation\",\"conventions\"]"));
			Assert.That(settingsJson, Does.Contain("\"objectValue\":{\"owner\":\"Faithlife\"}"));
			Assert.That(settingsJson, Does.Contain("\"nullValue\":null"));
		}
	}

	[Test]
	public async Task CommitModeReadsTextFromTopLevelYamlSettings()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/write-settings
			  settings:
			    text: ${{ readText("./body.txt") }}
			    message: prefix-${{ readText("./name.txt") }}-suffix
			""");
		repo.WriteFile(".github/body.txt", "body text");
		repo.WriteFile(".github/name.txt", "repo");
		WriteSettingsConvention(repo);
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);
		var settingsJson = await repo.ReadFileAsync("settings.json");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(settingsJson, Does.Contain("\"text\":\"body text\""));
			Assert.That(settingsJson, Does.Contain("\"message\":\"prefix-repo-suffix\""));
		}
	}

	[Test]
	public async Task CommitModeReadsTextRelativeToNestedCompositeYaml()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/parent
			""");
		repo.WriteFile(".github/conventions/parent/convention.yml", """
			conventions:
			- path: ../write-settings
			  settings:
			    text: ${{ readText("./body.txt") }}
			""");
		repo.WriteFile(".github/conventions/parent/body.txt", "nested body");
		WriteSettingsConvention(repo);
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);
		var settingsJson = await repo.ReadFileAsync("settings.json");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(settingsJson, Does.Contain("\"text\":\"nested body\""));
		}
	}

	[Test]
	public async Task CommitModeReadsTextFromRepositoryRootRelativePath()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/write-settings
			  settings:
			    text: ${{ readText("/docs/body.txt") }}
			""");
		repo.WriteFile("docs/body.txt", "root body");
		WriteSettingsConvention(repo);
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);
		var settingsJson = await repo.ReadFileAsync("settings.json");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(settingsJson, Does.Contain("\"text\":\"root body\""));
		}
	}

	[Test]
	public async Task CommitModeFailsWhenReadTextFileIsMissingBeforeApplyingAnyConvention()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/good
			- path: ./conventions/write-settings
			  settings:
			    text: ${{ readText("./missing.txt") }}
			""");
		repo.WriteFile(".github/conventions/good/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'good.txt') -Value 'good'
			""");
		WriteSettingsConvention(repo);
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(repo.FileExists("good.txt"), Is.False);
			Assert.That(repo.FileExists("settings.json"), Is.False);
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Initial commit."));
			Assert.That(result.StandardError, Does.Contain("missing.txt"));
		}
	}

	[Test]
	public async Task CommitModeFailsWhenReadTextArgumentIsNotJsonStringLiteral()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/write-settings
			  settings:
			    text: ${{ readText(settings.foo.bar) }}
			""");
		WriteSettingsConvention(repo);
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(result.StandardError, Does.Contain("JSON string literal path argument"));
		}
	}

	[Test]
	public async Task CommitModeFailsWhenReadTextEscapesRepositoryRoot()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/write-settings
			  settings:
			    text: ${{ readText("../../outside.txt") }}
			""");
		WriteSettingsConvention(repo);
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(result.StandardError, Does.Contain("escapes the repository root"));
		}
	}

	[Test]
	public async Task CommitModeFailsWhenLocalConventionPathEscapesContainingRepositoryRoot()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		var repoParentPath = Directory.GetParent(repo.RootPath)!.FullName;
		var outsideConventionName = $"outside-{Guid.NewGuid():N}";
		var outsideConventionPath = Path.Combine(repoParentPath, outsideConventionName);
		Directory.CreateDirectory(outsideConventionPath);

		try
		{
			await File.WriteAllTextAsync(
				Path.Combine(outsideConventionPath, "convention.ps1"),
				"""
				param([string] $configPath)
				Set-Content -Path (Join-Path $PWD 'should-not-exist.txt') -Value 'unexpected'
				""");
			repo.WriteFile(".github/conventions.yml", """
				conventions:
				- path: ./conventions/parent
				""");
			repo.WriteFile(
				".github/conventions/parent/convention.yml",
				$"conventions:{Environment.NewLine}- path: ../../../../{outsideConventionName}{Environment.NewLine}");
			await repo.CommitAllAsync("Initial commit.");

			var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(result.ExitCode, Is.Not.Zero);
				Assert.That(result.StandardError, Does.Contain("escapes the repository root"));
				Assert.That(repo.FileExists("should-not-exist.txt"), Is.False);
			}
		}
		finally
		{
			Directory.Delete(outsideConventionPath, recursive: true);
		}
	}

	[Test]
	public async Task CommitModeFailsWhenReadTextUsesNativeAbsolutePath()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Ignore("Drive-root absolute paths are Windows-specific.");

		using var repo = await TemporaryGitRepository.CreateAsync();
		var absolutePathLiteral = JsonSerializer.Serialize(Path.Combine(Path.GetTempPath(), "outside.txt"));
		repo.WriteFile(
			".github/conventions.yml",
			"conventions:\n"
			+ "- path: ./conventions/write-settings\n"
			+ "  settings:\n"
			+ $"    text: ${{{{ readText({absolutePathLiteral}) }}}}\n");
		WriteSettingsConvention(repo);
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(result.StandardError, Does.Contain("Native absolute path"));
		}
	}

	[Test]
	public async Task CommitModeStripsUtf8BomWhenReadingText()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/write-settings
			  settings:
			    text: ${{ readText("./body.txt") }}
			""");
		repo.WriteFile(".github/body.txt", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("bom text")]);
		WriteSettingsConvention(repo);
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);
		var settingsJson = await repo.ReadFileAsync("settings.json");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(settingsJson, Does.Contain("\"text\":\"bom text\""));
			Assert.That(settingsJson, Does.Not.Contain("\\uFEFF"));
		}
	}

	[Test]
	public async Task CommitModeFailsWhenReadTextFileIsNotUtf8()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/write-settings
			  settings:
			    text: ${{ readText("./body.txt") }}
			""");
		repo.WriteFile(".github/body.txt", [0xFF]);
		WriteSettingsConvention(repo);
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(result.StandardError, Does.Contain("not valid UTF-8 text"));
		}
	}

	[Test]
	public async Task CommitModeInterpolatesMultipleExpressionsAndMissingValuesInCompositeStrings()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/parent
			  settings:
			    owner: Faithlife
			    repo: RepoConventions
			    version: 10
			""");
		repo.WriteFile(".github/conventions/parent/convention.yml", """
			conventions:
			- path: ../write-settings
			  settings:
			    displayName: ${{ settings.owner }}/${{ settings.repo }}@v${{ settings.version }}
			    missingMessage: hello-${{ settings.missing }}-world
			""");
		repo.WriteFile(".github/conventions/write-settings/convention.ps1", """
			param([string] $configPath)
			$config = Get-Content -Raw $configPath | ConvertFrom-Json
			$config.settings | ConvertTo-Json -Compress -Depth 10 | Set-Content -Path (Join-Path $PWD 'settings.json')
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);
		var settingsJson = await repo.ReadFileAsync("settings.json");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(settingsJson, Does.Contain("\"displayName\":\"Faithlife/RepoConventions@v10\""));
			Assert.That(settingsJson, Does.Contain("\"missingMessage\":\"hello--world\""));
		}
	}

	[Test]
	public async Task CommitModeOmitsMissingExactValuesAndSplicesArraysFromCompositeConvention()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/parent
			  settings:
			    labels:
			      - automation
			      - conventions
			""");
		repo.WriteFile(".github/conventions/parent/convention.yml", """
			conventions:
			- path: ../write-settings
			  settings:
			    keep: yes
			    missingProp: ${{ settings.missing }}
			    items:
			      - before
			      - ${{ settings.labels }}
			      - ${{ settings.missing }}
			      - after
			""");
		repo.WriteFile(".github/conventions/write-settings/convention.ps1", """
			param([string] $configPath)
			$config = Get-Content -Raw $configPath | ConvertFrom-Json
			$config.settings | ConvertTo-Json -Compress -Depth 10 | Set-Content -Path (Join-Path $PWD 'settings.json')
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);
		var settingsJson = await repo.ReadFileAsync("settings.json");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(settingsJson, Does.Contain("\"keep\":\"yes\""));
			Assert.That(settingsJson, Does.Not.Contain("missingProp"));
			Assert.That(settingsJson, Does.Contain("\"items\":[\"before\",\"automation\",\"conventions\",\"after\"]"));
			Assert.That(settingsJson, Does.Not.Contain("[[\"automation\",\"conventions\"]]"));
		}
	}

	[Test]
	public async Task CommitModePropagatesSettingsThroughNestedCompositeConventions()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/outer
			  settings:
			    sdk:
			      version: 10
			    package:
			      id: RepoConventions
			""");
		repo.WriteFile(".github/conventions/outer/convention.yml", """
			conventions:
			- path: ../inner
			  settings:
			    version: ${{ settings.sdk.version }}
			    packageId: ${{ settings.package.id }}
			""");
		repo.WriteFile(".github/conventions/inner/convention.yml", """
			conventions:
			- path: ../write-settings
			  settings:
			    displayName: ${{ settings.packageId }} v${{ settings.version }}
			""");
		repo.WriteFile(".github/conventions/write-settings/convention.ps1", """
			param([string] $configPath)
			$config = Get-Content -Raw $configPath | ConvertFrom-Json
			$config.settings | ConvertTo-Json -Compress -Depth 10 | Set-Content -Path (Join-Path $PWD 'settings.json')
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);
		var settingsJson = await repo.ReadFileAsync("settings.json");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(settingsJson, Does.Contain("\"displayName\":\"RepoConventions v10\""));
		}
	}

	[Test]
	public async Task CommitModeFailsWhenCompositeSettingsPropagationHitsTypeMismatch()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/parent
			  settings:
			    sdk: 10
			""");
		repo.WriteFile(".github/conventions/parent/convention.yml", """
			conventions:
			- path: ../write-settings
			  settings:
			    version: ${{ settings.sdk.version }}
			""");
		repo.WriteFile(".github/conventions/write-settings/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'should-not-exist.txt') -Value 'unexpected'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(repo.FileExists("should-not-exist.txt"), Is.False);
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Initial commit."));
			Assert.That(result.StandardError, Does.Contain("../write-settings"));
			Assert.That(result.StandardError, Does.Contain("${{ settings.sdk.version }}"));
			Assert.That(result.StandardError, Does.Contain("non-object value"));
		}
	}

	[Test]
	public async Task CommitModeBuildsEntireConventionPlanBeforeApplyingAnyConvention()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/good
			- path: ./conventions/parent
			""");
		repo.WriteFile(".github/conventions/good/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'good.txt') -Value 'good'
			""");
		repo.WriteFile(".github/conventions/parent/convention.yml", """
			conventions:
			- path: ../missing
			""");
		repo.WriteFile(".github/conventions/parent/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'parent.txt') -Value 'parent'
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(repo.FileExists("good.txt"), Is.False);
			Assert.That(repo.FileExists("parent.txt"), Is.False);
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Initial commit."));
			Assert.That(result.StandardOutput, Does.Not.Contain("Convention good\n"));
			Assert.That(result.StandardError, Does.Contain("../missing"));
		}
	}

	[Test]
	public async Task CommitModeCleansUpFailedConventionWithoutRevertingPreviousCommit()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: ./conventions/good
			- path: ./conventions/bad
			""");
		repo.WriteFile(".github/conventions/good/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'good.txt') -Value 'good'
			""");
		repo.WriteFile(".github/conventions/bad/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'bad.txt') -Value 'bad'
			exit 1
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(["apply"], repo.RootPath);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(repo.FileExists("good.txt"), Is.True);
			Assert.That(repo.FileExists("bad.txt"), Is.False);
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Apply convention good"));
			Assert.That(await repo.GetWorkingTreeStatusAsync(), Is.Empty);
		}
	}

	[Test]
	public async Task CommitModeAppliesRemoteExecutableConventionAndCreatesCommit()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		using var remoteRepo = await TemporaryGitRepository.CreateAsync();
		remoteRepo.WriteFile("conventions/add-file/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'remote.txt') -Value 'remote'
			""");
		await remoteRepo.CommitAllAsync("Initial remote commit.");

		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: local-test/remote-conventions/conventions/add-file@main
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(
			["apply"],
			repo.RootPath,
			request =>
				request is { Owner: "local-test", Repository: "remote-conventions" }
					? remoteRepo.GetRepositoryUri()
					: throw new AssertionException($"Unexpected remote repository {request.Owner}/{request.Repository}."));

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("remote.txt"), Is.True);
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Apply convention add-file"));
		}
	}

	[Test]
	public async Task CommitModeAppliesRemoteConventionAtRequestedRef()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		using var remoteRepo = await TemporaryGitRepository.CreateAsync();
		remoteRepo.WriteFile("conventions/versioned/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'version.txt') -Value 'v1'
			""");
		await remoteRepo.CommitAllAsync("Version 1.");
		await remoteRepo.CreateTagAsync("v1");
		remoteRepo.WriteFile("conventions/versioned/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'version.txt') -Value 'main'
			""");
		await remoteRepo.CommitAllAsync("Version main.");

		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: local-test/remote-conventions/conventions/versioned@v1
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(
			["apply"],
			repo.RootPath,
			request =>
				request is { Owner: "local-test", Repository: "remote-conventions" }
					? remoteRepo.GetRepositoryUri()
					: throw new AssertionException($"Unexpected remote repository {request.Owner}/{request.Repository}."));

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(await repo.ReadFileAsync("version.txt"), Does.Contain("v1"));
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Apply convention versioned"));
		}
	}

	[Test]
	public async Task CommitModeAppliesRemoteCompositeConventionBeforeItsScript()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		using var remoteRepo = await TemporaryGitRepository.CreateAsync();
		remoteRepo.WriteFile("conventions/parent/convention.yml", """
			conventions:
			- path: ../child
			""");
		remoteRepo.WriteFile("conventions/child/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'remote-child.txt') -Value 'child'
			""");
		remoteRepo.WriteFile("conventions/parent/convention.ps1", """
			param([string] $configPath)
			if (-not (Test-Path (Join-Path $PWD 'remote-child.txt'))) { throw 'child missing' }
			Set-Content -Path (Join-Path $PWD 'remote-parent.txt') -Value 'parent'
			""");
		await remoteRepo.CommitAllAsync("Initial remote commit.");

		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: local-test/remote-conventions/conventions/parent@main
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(
			["apply"],
			repo.RootPath,
			LocalTestRemoteRepositoryUrlResolver(remoteRepo));
		var normalizedOutput = NormalizeConventionOutput(result.StandardOutput);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("remote-child.txt"), Is.True);
			Assert.That(repo.FileExists("remote-parent.txt"), Is.True);
			Assert.That(normalizedOutput, Does.Contain("\nConvention child < parent\nCreated 1 commit for convention child.\n\nConvention parent\n"));
			Assert.That(result.StandardOutput, Does.Contain("Created 1 commit for convention parent."));
			Assert.That(await repo.GetRecentCommitMessagesAsync(2), Is.EqualTo(s_parentThenChildCommitMessages));
		}
	}

	[Test]
	public async Task CommitModeSupportsRepositoryRootRelativeConventionPathsWithinRemoteCompositeConvention()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		using var remoteRepo = await TemporaryGitRepository.CreateAsync();
		remoteRepo.WriteFile("conventions/parent/convention.yml", """
			conventions:
			- path: /conventions/root-relative-child
			""");
		remoteRepo.WriteFile("conventions/root-relative-child/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'remote-root-relative.txt') -Value 'created'
			""");
		await remoteRepo.CommitAllAsync("Initial remote commit.");

		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: local-test/remote-conventions/conventions/parent@main
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(
			["apply"],
			repo.RootPath,
			LocalTestRemoteRepositoryUrlResolver(remoteRepo));

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("remote-root-relative.txt"), Is.True);
			Assert.That(result.StandardOutput, Does.Contain("Convention root-relative-child"));
		}
	}

	[Test]
	public async Task CommitModeReadsTextFromRelativeAndRootPathsWithinRemoteConventionRepository()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		using var remoteRepo = await TemporaryGitRepository.CreateAsync();
		remoteRepo.WriteFile("conventions/read-settings/convention.yml", """
			conventions:
			- path: ./write-settings
			  settings:
			    relativeText: ${{ readText("./body.txt") }}
			    rootText: ${{ readText("/docs/body.txt") }}
			""");
		remoteRepo.WriteFile("conventions/read-settings/body.txt", "remote relative body");
		remoteRepo.WriteFile("docs/body.txt", "remote root body");
		remoteRepo.WriteFile("conventions/read-settings/write-settings/convention.ps1", """
			param([string] $configPath)
			$config = Get-Content -Raw $configPath | ConvertFrom-Json
			$config.settings | ConvertTo-Json -Compress -Depth 10 | Set-Content -Path (Join-Path $PWD 'settings.json')
			""");
		await remoteRepo.CommitAllAsync("Initial remote commit.");

		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: local-test/remote-conventions/conventions/read-settings@main
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(
			["apply"],
			repo.RootPath,
			LocalTestRemoteRepositoryUrlResolver(remoteRepo));
		var settingsJson = await repo.ReadFileAsync("settings.json");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(settingsJson, Does.Contain("\"relativeText\":\"remote relative body\""));
			Assert.That(settingsJson, Does.Contain("\"rootText\":\"remote root body\""));
		}
	}

	[Test]
	public async Task CommitModeUsesRepositoryNameWhenRemoteConventionIsAtRepositoryRoot()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		using var remoteRepo = await TemporaryGitRepository.CreateAsync();
		remoteRepo.WriteFile("convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'remote-root.txt') -Value 'root'
			""");
		await remoteRepo.CommitAllAsync("Initial remote commit.");

		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: local-test/remote-conventions@main
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(
			["apply"],
			repo.RootPath,
			LocalTestRemoteRepositoryUrlResolver(remoteRepo));

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("remote-root.txt"), Is.True);
			Assert.That(await repo.GetHeadCommitMessageAsync(), Is.EqualTo("Apply convention remote-conventions"));
		}
	}

	[Test]
	public async Task CommitModeSkipsCycleDetectedAcrossRemoteReferences()
	{
		using var repo = await TemporaryGitRepository.CreateAsync();
		using var remoteRepo = await TemporaryGitRepository.CreateAsync();
		remoteRepo.WriteFile("conventions/cycle/convention.yml", """
			conventions:
			- path: local-test/remote-conventions/conventions/cycle@main
			""");
		remoteRepo.WriteFile("conventions/cycle/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'cycle.txt') -Value 'cycle'
			""");
		remoteRepo.WriteFile("conventions/after/convention.ps1", """
			param([string] $configPath)
			Set-Content -Path (Join-Path $PWD 'after.txt') -Value 'after'
			""");
		await remoteRepo.CommitAllAsync("Initial remote commit.");

		repo.WriteFile(".github/conventions.yml", """
			conventions:
			- path: local-test/remote-conventions/conventions/cycle@main
			- path: local-test/remote-conventions/conventions/after@main
			""");
		await repo.CommitAllAsync("Initial commit.");

		var result = await CliInvocation.InvokeAsync(
			["apply"],
			repo.RootPath,
			LocalTestRemoteRepositoryUrlResolver(remoteRepo));

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(repo.FileExists("cycle.txt"), Is.True);
			Assert.That(repo.FileExists("after.txt"), Is.True);
			Assert.That(result.StandardOutput, Does.Contain("skipped (cycle detected)"));
		}
	}

	private static Func<RemoteRepositoryUrlRequest, string> LocalTestRemoteRepositoryUrlResolver(TemporaryGitRepository remoteRepo) =>
		request =>
			request is { Owner: "local-test", Repository: "remote-conventions" }
				? remoteRepo.GetRepositoryUri()
				: throw new AssertionException($"Unexpected remote repository {request.Owner}/{request.Repository}.");

	private static void WriteSettingsConvention(TemporaryGitRepository repo)
	{
		repo.WriteFile(".github/conventions/write-settings/convention.ps1", """
			param([string] $configPath)
			$config = Get-Content -Raw $configPath | ConvertFrom-Json
			$config.settings | ConvertTo-Json -Compress -Depth 10 | Set-Content -Path (Join-Path $PWD 'settings.json')
			""");
	}

	private static async Task WaitForFileAsync(TemporaryGitRepository repo, string relativePath)
	{
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (!repo.FileExists(relativePath) && DateTime.UtcNow < deadline)
			await Task.Delay(50);

		Assert.That(repo.FileExists(relativePath), Is.True, $"{relativePath} was not created.");
	}

	private static void DeleteDirectoryWithRetries(string directoryPath)
	{
		if (!Directory.Exists(directoryPath))
			return;

		ClearAttributes(directoryPath);

		for (var attempt = 0; attempt < 10; attempt++)
		{
			try
			{
				Directory.Delete(directoryPath, recursive: true);
				return;
			}
			catch (IOException) when (attempt < 9)
			{
				Thread.Sleep(100);
			}
			catch (UnauthorizedAccessException) when (attempt < 9)
			{
				Thread.Sleep(100);
			}
		}
	}

	private static void ClearAttributes(string rootPath)
	{
		foreach (var directoryPath in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories))
			File.SetAttributes(directoryPath, FileAttributes.Normal);

		foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
			File.SetAttributes(filePath, FileAttributes.Normal);

		File.SetAttributes(rootPath, FileAttributes.Normal);
	}

	private static string NormalizeConventionOutput(string output)
	{
		var normalizedOutput = output.ReplaceLineEndings("\n");
		var normalizedLines = new List<string>();

		foreach (var line in normalizedOutput.Split('\n'))
		{
			if (line == "::endgroup::")
				continue;

			if (line.StartsWith("::group::", StringComparison.Ordinal))
			{
				normalizedLines.Add("");
				normalizedLines.Add(line["::group::".Length..]);
			}
			else
			{
				normalizedLines.Add(line);
			}
		}

		return string.Join("\n", normalizedLines);
	}

	private static readonly string[] s_parentThenChildCommitMessages = ["Apply convention parent", "Apply convention child"];
	private static readonly string[] s_selfCreatedCommitMessages = ["Second self-created commit.", "First self-created commit."];
	private static readonly string[] s_amendedSharedCommitMessages = ["Update shared files", "Initial commit."];
}
