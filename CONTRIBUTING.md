# Contributing

Issues and pull requests are welcome.

- **An installer runs on customer machines.** A change to the install, upgrade, rollback or
  uninstall steps needs a note in the pull request on which Windows version it was tried on,
  fresh install and upgrade.
- **No product specifics.** Names, folders, services and texts of a product belong in its own
  `ProductDefinition`. If a change only makes sense for one product, it does not belong here.
- **Tests run without installing anything.** `dotnet test` must not touch Program Files, the
  registry or the service manager.
- The build treats warnings as errors and enforces the code style in `.editorconfig`.

```
dotnet build
dotnet test
```

By contributing you agree that your contribution is licensed under the Apache License 2.0.
