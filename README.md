# Postfold

Postfold is an open-source email client built with .NET and Avalonia, licensed under MPL-2.0.
It is in development, with Windows as the first target.

## Build and run

Install the .NET 10 SDK (10.0.402 or newer), then run from the repository root:

```sh
dotnet restore Postfold.slnx --locked-mode
dotnet build Postfold.slnx --configuration Release --no-restore
dotnet run --project Postfold.Desktop --configuration Release --no-build
```

## Contribute

See [CONTRIBUTING.md](CONTRIBUTING.md) for project conventions, tests and validation commands.
