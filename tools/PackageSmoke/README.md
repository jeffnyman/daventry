# Package smoke test

Checks the NuGet packages the way a consumer would, from a local feed rather than project references:

```
tools\PackageSmoke\run.ps1 -Game "<King's Quest 1 (SCI) folder>" -BrowserGame "<King's Quest 1 (AGI) folder>"
```

The script packs the repository into `artifacts/packages` (`-SkipPack` reuses the last pack,
`-VersionSuffix alpha.2` sets the prerelease label), then:

1. `ConsoleSmoke` restores `Daventry.Agi` and `Daventry.Sci` from that feed, plays the game headless
   for 600 ticks and writes `artifacts/console.png`; it fails if nothing was drawn.
2. `Daventry.Player` is installed as a dotnet tool into `artifacts/tool` and takes `artifacts/tool.png`.
3. `BlazorSmoke`, the smallest page that uses `Daventry.Host.Browser`, is published to
   `artifacts/blazor`; the host's JavaScript module must appear under `_content/`.
4. With `-BrowserGame`, Node and Chrome, the game is copied next to the published page with a
   manifest and headless Chrome plays it through `tools/BrowserSmoke`, printing the page's status,
   the audio counters and the number of lit canvas channels, and saving `artifacts/browser.png`.

Restores use `artifacts/nuget-cache` as the package cache, so a re-packed prerelease with the same
version is never served from the global cache. The two projects have an empty `Directory.Build.props`
and `Directory.Packages.props` so the repository's build settings do not reach them, and they are not
part of the solution.
