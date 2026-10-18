<p align="center">
  <img alt="Typedown Logo" src="./logo.png" width="100px" />
  <h1 align="center">Typedown (Fork)</h1>
</p>

> [!NOTE]
> This is a personal fork of [byxiaozhi/Typedown](https://github.com/byxiaozhi/Typedown) with custom changes.
> It is **not** the official Typedown and is not affiliated with or endorsed by the original author.
> Binaries built from this repository are not the Microsoft Store version.
> The official release is available in the [Microsoft Store](https://apps.microsoft.com/detail/9p8tcw4h2hb4).
>
> Many thanks to [ZZF (byxiaozhi)](https://github.com/byxiaozhi) for creating Typedown and releasing it under the MIT License.

## Changes in this fork

- _No changes yet._

## About Typedown

Typedown is a lightweight Markdown editor designed specifically for the Windows platform. With the WinUI framework, it provides users with a seamless interface and efficient editing experience that perfectly matches the operating system. Whether you're writing technical documents, academic papers, or blog posts, Typedown is your go-to assistant!

## Building from source

### 1. Prerequisites
[Visual Studio 2022](https://visualstudio.microsoft.com/vs/) with the following individual components:
  - .NET Core 3.1 SDK
  - Git for Windows

[Node.js](https://nodejs.org/) with the following global packages:
  - [yarn](https://yarnpkg.com/)

### 2. Clone the repository
```ps
git clone https://github.com/holgerspiering/Typedown
```

This will create a local copy of the repository.

### 3. Build the project
First go to the directory `Typedown\Dev\Typedown.Editor` and run `yarn && yarn build`

```ps
cd Typedown\Dev\Typedown.Editor
yarn && yarn build
```

After finishing the compilation of `Typedown.Editor`, you can see the generated product in the directory `Typedown\Dev\Typedown\Resources\Statics`.

Then use VisualStudio 2022 to open `Typedown\Typedown.sln`, right-click on the Typedown project and select Set as Startup Project.

In the top pane, select the solution configuration you want to build in, the difference between these configurations is as follows
- Debug: The `Typedown.Editor` will be accessed using the http://localhost:3000 address, to use this configuration you need to also start the Typedown.Editor project using yarn start in the Typedown\Dev\Typedown.Editor directory.
- Debug_Local: The `Typedown.Editor` will be accessed using the compiled product (Typedown\Dev\Typedown\Resources\Statics)
- Release: Used when releasing a project

Then select the platform you want to build on (x64, x86, or arm64) and click Run!

### Issues
Issues specific to the changes in this fork belong in [this repository](https://github.com/holgerspiering/Typedown/issues).
Issues with Typedown itself belong in the [original repository](https://github.com/byxiaozhi/Typedown/issues).

## License
MIT. See [LICENSE](LICENSE). Original work Copyright (c) 2022 ZZF.
