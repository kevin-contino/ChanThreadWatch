# Chan Thread Watch

This project is a fork of the discontinued Chan Thread Watch. All credit goes to the original developer.  
You can DOWNLOAD this program here: [https://github.com/SuperGouge/ChanThreadWatch/releases](https://github.com/SuperGouge/ChanThreadWatch/releases)  
You can find the original official site here: [https://sites.google.com/site/chanthreadwatch/](https://sites.google.com/site/chanthreadwatch/)

## Wiki

For documentation, changelog and any other information, please visit the wiki: [https://github.com/SuperGouge/ChanThreadWatch/wiki](https://github.com/SuperGouge/ChanThreadWatch/wiki)

## Building and testing

Requires Visual Studio (or Build Tools) MSBuild with the .NET SDK component, and the .NET SDK for `dotnet test`. The .NET Framework 4.8 reference assemblies come from NuGet, so the Developer Pack is not needed.

```
msbuild ChanThreadWatch.sln -restore -p:Configuration=Release
dotnet test ChanThreadWatch.sln -c Release --no-build
```

`dotnet build` alone does not work: the SDK's MSBuild cannot compile the forms' non-string `.resx` resources.

## License

Chan Thread Watch was written by J.D. Purcell (JDP) and is licensed under the MIT License, as published in the original repository ([jdpurcell/ChanThreadWatch](https://github.com/jdpurcell/ChanThreadWatch)). See [LICENSE.txt](LICENSE.txt).
