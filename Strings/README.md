# Localization (i18n)

The app localizes UI strings by swapping a string `ResourceDictionary` at runtime —
the same mechanism `ThemeService` uses for color themes. English (`en`) is the only
language today; the system is built so more can be dropped in without code changes.

## How it works

- `Strings/en.xaml` holds every localized string as `<sys:String x:Key="Str.Area.Name">`.
- `Services/LocalizationService.cs` (mirrors `ThemeService`) swaps the active
  `Strings/<culture>.xaml` into `Application.Resources`, persists the choice to
  `GlobalSettings.Language`, sets the thread culture (so date/number `StringFormat`
  render per-locale), and raises `LanguageChanged`.
- Because XAML references strings via `{DynamicResource ...}`, switching language
  updates the visible UI live.

## Key convention

`Str.<Area>.<Name>` — e.g. `Str.Common.Cancel`, `Str.Dashboard.Title`, `Str.AddApp.AppName`.
Group keys by area with comments. Reuse `Str.Common.*` for shared words (OK, Cancel, Browse…).

## Adding a string

1. Add `<sys:String x:Key="Str.Area.Name">English text</sys:String>` to `Strings/en.xaml`.
2. **In XAML:** `Text="{DynamicResource Str.Area.Name}"` (or `Content=`, `ToolTip=`, `Header=`, `Title=`).
3. **In C# (DI):** inject `ILocalizationService` and call `_loc.Get("Str.Area.Name")`,
   or `_loc.Get("Str.Area.Name", arg0, arg1)` for `{0}`/`{1}` placeholders.
4. **In C# (non-DI, e.g. a dialog code-behind):** use `TryFindResource("Str.Area.Name") as string`.

Missing keys return the key text itself (no crash), so untranslated/typo'd keys are visible.

## Adding a language

1. Copy `Strings/en.xaml` to `Strings/<culture>.xaml` (e.g. `fr.xaml`, `de.xaml`, `es.xaml`).
2. Translate the **values**; keep the **keys** identical.
3. Add the language to `LanguageOptions` in `ViewModels/GlobalOptionsViewModel.cs`
   (display name → culture code). That's it — no other code changes.

`.xaml` files under the project are compiled as WPF `Page` resources automatically, so a
new `Strings/<culture>.xaml` is resolvable via `/Strings/<culture>.xaml` with no `.csproj` edit.

## Status

Infrastructure + pilot screens are done: `MainWindow`, `DashboardView`,
`AddEditAppDialog`, and `AppEntryViewModel`. Remaining Views/Dialogs/ViewModels are
migrated incrementally using the pattern above. The `SetupBootstrapper` installer is a
separate app and is not yet localized.
