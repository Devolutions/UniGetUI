If (Get-Command scoop -ErrorAction SilentlyContinue) {
    Write-Output "Scoop is already installed."
    exit 1
}

Write-Output "Installing scoop..."

iex "& {$(irm get.scoop.sh)} -RunAsAdmin"

$Env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")  

If (-Not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Output "Installing git..."
    scoop install git
}

scoop install scoop-search

Write-Output "Done!"
