-------------------------------------------------------------
This repository **respects** people, regardless of their race, gender, religion, height, or culture. Any user who posts offensive or disrespectful content regarding race, gender, religion, height, or culture **will be immediately banned from this repository**. No exception will be made.

-------------------------------------------------------------

### DO NOT publish garbage PRs to farm Crypto AirDrops. Any user suspected of this action will get banned. Submitting broken code wastes the contributors' time, who have to spend their free time reviewing, fixing, and testing code that does not even compile does not break other functionality, or does not introduce any changes at all.  

---------------------------------



# Contributing guidelines:

Before reading: All of the rules below are guidelines, which means that they should be followed when possible. Please do not take them literally.

## Discussions:
 - This is the place to post any questions/doubts regarding UniGetUI. Issues and feature requests should be posted in the [issues section](https://github.com/Devolutions/UniGetUI/issues).

## Issues and feature requests:

#### Issues:
 - Please use the BUG/ISSUE template
 - Please be clear when describing issues.
 - Please fill out the form and DO NOT send empty issues with the information on the title.
 - Please search for possible duplicates, and only post new content. (As stated in the BIG/ISSUE template).
 - Please make sure to preceed titles with the `[BUG/ISSUE]` string, so they can be easily identified.

#### Feature requests:
- Please use the FEATURE REQUEST template
 - Please detail how the feature should work. Please be as specific as possible.
 - Some features are difficult and might take some time to get implemented. This project is made in the contributor's free time, so please do not post messages asking for ETAs or similar. Every feature request will be considered.
 - Please check for duplicates as said in the FEATURE REQUEST template.
 - Please make sure to preceed titles with the `[FEATURE REQUEST]` string, so they can be easily identified.

## Pull requests:
 - Please specify, either in the title or in the PR body text, the changes done. 
 - _Improvements_ pull request should have a list of the changes done in the body message, whether they are listed in the commits or not.
 - Draft pull requests should be properly identified as [draft pull requests](https://github.blog/2019-02-14-introducing-draft-pull-requests/) to avoid confusion.
 - When modifying/coding, please follow the guidelines below:

## Formatting:
 - Run `pwsh ./scripts/install-git-hooks.ps1` once after cloning to enable the repository pre-commit hook.
- The pre-commit hook runs `dotnet format whitespace src --folder` on staged files under `src` when the `dotnet` CLI is available, and stops the commit if it had to rewrite files so you can review the changes and commit again.
- CI enforces whitespace formatting with `dotnet format whitespace src --folder --verify-no-changes` and code-style verification with `dotnet format style src/UniGetUI.Windows.slnx --no-restore --verify-no-changes` in `.github/workflows/dotnet-test.yml`.
 - The pre-commit hook intentionally does not run `dotnet format style` because solution loading makes it take roughly the same time for one staged C# file as for the full solution.
- If you want to check the same style rules locally before pushing, run `dotnet format style src/UniGetUI.Windows.slnx --no-restore --verify-no-changes` from the repository root.
 - If you want to prepare a dedicated formatting-only commit, run `dotnet format whitespace src --folder` from the repository root.

## Coding:
 - As a repository standard, every function and variable name should use camelCase.
   - Correct usage: `updatesCount = 0`, `def searchForUpdates(packageManager):`
   - Incorrect usage: `updates_count = 0`, `def searchforupdates(package_manager):`
 - Constants should be written in capital letters, using underscores for spaces:
   - Example: `SYSTEM_DEFAULT_LOCALE = "ca-ES"`
 - Please specify, when possible, variable data types and function return types. More info [here](https://python.plainenglish.io/specifying-data-types-in-python-c182fda3bf43)
 - Try to add spaces and empty newlines to make code more human-readable.

## Commits:
 - Commits must include only changes on one feature or section of the code. Let's say, you have fixed an issue regarding localization and added a new entry in the settings section to change update frequency, those two changes must be committed separately.
 - The code in each commit should be executable. Please do not leave work unfinished across commits, or, if it is needed, let the code be executed without errors.
 - Commit names must be self-explanatory, and, if applicable, must reference the corresponding issue.
