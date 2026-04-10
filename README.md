# WARNING: **wingetui<sub>•</sub>com** and **unigetui<sub>•</sub>com** are fake websites hosted by a third-party. please do NOT trust them
<br>

## <img src="media/icon.png" height="40">Devolutions UniGetUI

> [!IMPORTANT]
> **Major announcement:** UniGetUI has entered its next chapter with Devolutions.
> Read the [blog post](https://devolutions.net/blog/2026/03/unigetui-enters-its-next-chapter-with-devolutions/) and the [official press release](https://www.globenewswire.com/news-release/2026/03/10/3253012/0/en/Devolutions-Acquires-UniGetUI-Strengthening-Security-and-Enterprise-Readiness.html).

[![Downloads](https://img.shields.io/github/downloads/Devolutions/UniGetUI/total?style=for-the-badge)](https://github.com/Devolutions/UniGetUI/releases/latest/download/UniGetUI.Installer.exe)
[![Release Version Badge](https://img.shields.io/github/v/release/Devolutions/UniGetUI?style=for-the-badge)](https://github.com/Devolutions/UniGetUI/releases)
[![Issues Badge](https://img.shields.io/github/issues/Devolutions/UniGetUI?style=for-the-badge)](https://github.com/Devolutions/UniGetUI/issues)
[![Closed Issues Badge](https://img.shields.io/github/issues-closed/Devolutions/UniGetUI?color=%238256d0&style=for-the-badge)](https://github.com/Devolutions/UniGetUI/issues?q=is%3Aissue+is%3Aclosed)<br>
UniGetUI is an intuitive GUI for the most common CLI package managers on Windows 10 and 11, including [WinGet](https://learn.microsoft.com/en-us/windows/package-manager/), [Scoop](https://scoop.sh/), [Chocolatey](https://chocolatey.org/), [pip](https://pypi.org/), [npm](https://www.npmjs.com/), [.NET Tool](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-install), [PowerShell Gallery](https://www.powershellgallery.com/), and more.
With UniGetUI, you can discover, install, update, and uninstall software from multiple package managers through one interface.

![image](https://github.com/user-attachments/assets/7cb447ca-ee8b-4bce-8561-b9332fb0139a)
View more screenshots [here](#screenshots)

Check out the [Package Managers](#package-managers) section for more details!

**Disclaimer:** UniGetUI is not affiliated with the package managers it integrates with. Packages are provided by third parties, so review sources and publishers before installation.

![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/Devolutions/UniGetUI/dotnet-test.yml?branch=main&style=for-the-badge&label=Tests)<br>


> [!CAUTION]
> **The official website for UniGetUI is [https://devolutions.net/unigetui/](https://devolutions.net/unigetui/).**<br>
> **The official source repository is [https://github.com/Devolutions/UniGetUI](https://github.com/Devolutions/UniGetUI).**<br>
> **Any other website should be considered unofficial, despite what they may say.**

🔒 Found a security issue? Please report it via the [Devolutions security page](https://devolutions.net/security/)

## Project stewardship

UniGetUI was created by Martí Climent and is now maintained by Devolutions. The project remains free, open source, and MIT-licensed. Devolutions' stewardship brings long-term investment, structured governance, stronger security processes, and a roadmap for broader enterprise readiness while keeping UniGetUI standalone and community-driven.

Read more in the [Devolutions announcement](https://devolutions.net/blog/2026/03/unigetui-enters-its-next-chapter-with-devolutions/) and the [official press release](https://www.globenewswire.com/news-release/2026/03/10/3253012/0/en/Devolutions-Acquires-UniGetUI-Strengthening-Security-and-Enterprise-Readiness.html).

## Table of contents
 - **[UniGetUI Homepage](https://devolutions.net/unigetui/)**
 - [Table of contents](#table-of-contents)
 - [Installation](#installation)
 - [Update UniGetUI](#update-unigetui)
 - [Project stewardship](#project-stewardship)
 - [Features](#features)
 - [Package Managers](#package-managers)
 - [Translations](TRANSLATION.md)
 - [Contributors](#contributors)
 - [Screenshots](#screenshots)
 - [Frequently Asked Questions](#frequently-asked-questions)
 - [Command-line Arguments](cli-arguments.md)

## Installation
<p>There are multiple ways to install UniGetUI — choose whichever one you prefer!</p>
 
### Microsoft Store installation (recommended)
<a href="https://apps.microsoft.com/detail/xpfftq032ptphf"><img alt="alt_text" width="240px" src="https://get.microsoft.com/images/en-us%20dark.svg" /></a> 

 
### Download UniGetUI installer:
![GitHub Release](https://img.shields.io/github/v/release/Devolutions/UniGetUI?style=for-the-badge)
<p align="left"><b><a href="https://github.com/Devolutions/UniGetUI/releases/latest/download/UniGetUI.Installer.exe">Click here to download UniGetUI</a></b></p>


### Install UniGetUI via WinGet:
![WinGet Package Version](https://img.shields.io/winget/v/Devolutions.UniGetUI?style=for-the-badge)
```cmd
winget install --exact --id Devolutions.UniGetUI --source winget
```


### Install UniGetUI via Scoop:
![Scoop version](https://img.shields.io/scoop/v/unigetui?bucket=extras&style=for-the-badge)
```cmd
scoop bucket add extras
scoop install extras/unigetui
```

### Install UniGetUI via Chocolatey:
![Chocolatey Version](https://img.shields.io/chocolatey/v/wingetui?style=for-the-badge)
```cmd
choco install wingetui
```


## Update UniGetUI

UniGetUI has a built-in autoupdater. However, it can also be updated like any other package within UniGetUI (since UniGetUI is available from WinGet, Scoop and Chocolatey).


## Features

 - Install, update, and remove software from your system easily at one click: UniGetUI combines the packages from the most used package managers for windows: Winget, Chocolatey, Scoop, Pip, Npm and .NET Tool.
 - Discover new packages and filter them to easily find the package you want.
 - View detailed metadata about any package before installing it. Get the direct download URL or the name of the publisher, as well as the size of the download.
 - Easily bulk-install, update, or uninstall multiple packages at once selecting multiple packages before performing an operation
 - Automatically update packages, or be notified when updates become available. Skip versions or completely ignore updates on a per-package basis.
 - The system tray icon will also show the available updates and installed packages, to efficiently update a program or remove a package from your system.
 - Easily customize how and where packages are installed. Select different installation options and switches for each package. Install an older version or force to install a 32 bit architecture. \[But don't worry, those options will be saved for future updates for this package*]
 - Share packages with your friends using generated package links.
 - Export custom lists of packages to then import them to another machine and install those packages with previously specified, custom installation parameters. Setting up machines or configuring a specific software setup has never been easier.
 - Backup your packages to a local file to easily recover your setup in a matter of seconds when migrating to a new machine*

## Package Managers

**NOTE:** All package managers do support basic install, update, and uninstall processes, as well as checking for updates, finding new packages, and retrieving details from a package.

![image](media/supported-managers.svg)

✅: Supported on UniGetUI<br>
☑️: Not directly supported but can be easily achieved<br>
⚠️: May not work in some cases<br>
❌: Not supported by the Package Manager<br>
<br>

## Translations

UniGetUI translations are maintained directly in this repository. For the current language list, completion status, and per-language contributor attributions, see [TRANSLATION.md](TRANSLATION.md). If you spot a translation issue or want to improve a locale, please open an issue or submit a pull request.

## Screenshots
 
![image](media/UniGetUI_1.png)

![image](media/UniGetUI_2.png)

![image](media/UniGetUI_3.png)

![image](media/UniGetUI_4.png)

![image](media/UniGetUI_5.png)

![image](media/UniGetUI_6.png)

![image](media/UniGetUI_7.png)

![image](media/UniGetUI_8.png)

![image](media/UniGetUI_9.png)

![image](media/UniGetUI_10.png)


## Contributions
UniGetUI continues to grow thanks to its community of contributors. Devolutions is grateful to everyone who contributes code, translations, documentation, testing, and feedback to the project.<br><br>

[![Contributors](https://contrib.rocks/image?repo=Devolutions/UniGetUI)](https://github.com/Devolutions/UniGetUI/graphs/contributors)<br><br>


## Frequently asked questions

**Q: I am unable to install or upgrade a specific Winget package! What should I do?**<br>

A: This is likely an issue with Winget rather than UniGetUI. 

Please check if it's possible to install/upgrade the package through PowerShell or the Command Prompt by using the commands `winget upgrade` or `winget install`, depending on the situation (for example: `winget upgrade --id Microsoft.PowerToys`). 

If this doesn't work, consider asking for help at [Winget's project page](https://github.com/microsoft/winget-cli).<br>

#

**Q: The name of a package is trimmed with ellipsis — how do I see its full name/id?**<br>

A: This is a known limitation of Winget. 

For more details, see this issue: https://github.com/microsoft/winget-cli/issues/2603.<br>

#

**Q: My antivirus is telling me that UniGetUI is a virus! / My browser is blocking the download of UniGetUI!**<br>

A: A common reason apps (i.e., executables) get blocked and/or detected as a virus — even when there's nothing malicious about them, like in the case of UniGetUI — is because a relatively large amount of people are not using them.

Combine that with the fact that you might be downloading something recently released, and blocking unknown apps is in many cases a good precaution to take to prevent actual malware.

Since UniGetUI is open source and safe to use, whitelist the app in the settings of your antivirus/browser.<br>

#

**Q: Are Winget/Scoop packages safe?**<br>

A: UniGetUI, Microsoft, and Scoop aren't responsible for the packages available for download, which are provided by third parties and can theoretically be compromised.

Microsoft has implemented a few checks for the software available on Winget to mitigate the risks of downloading malware. Even so, it's recommended that you only download software from trusted publishers. 

<br><p align="center"><i>Check out the <a href="https://github.com/Devolutions/UniGetUI/wiki">Wiki</a> for more information!</i></p>

## Command-line parameters:

Check out the full list of parameters [here](cli-arguments.md)
