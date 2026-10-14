# Shared localization layer

The host frontend and backend share `Localization/zh-CN.json` and
`Localization/en-US.json` for common UI text only. Plugin-specific text belongs
to each plugin's own `Localization` directory, not these host catalogs.
Keys are stable, flat identifiers; values are display text.

## Frontend

Import `t`, `locale`, `setLocale`, `fieldLabel`, `formatDateTime` or
`formatNumber` from `@/i18n`. For example, `t('common.save')` and
`t('login.wait', { seconds: 30 })`. Vue I18n handles interpolation. Use plain
text bindings, not `v-html`. The language selector persists `lywaf_locale`
and updates the document language without reloading forms.

Management requests send `Accept-Language`. Unsupported preferences fall back
to Chinese. `translateText` is a temporary bridge for exact legacy display
labels, not a translator for arbitrary text or configuration data.

## Backend

`TextLocalizer.Get("api.internalError")` resolves stable resource keys.
`TextLocalizer.Display(text)` adapts known legacy messages. Management error
envelopes use this bridge while preserving error codes, trace IDs and details.
New endpoints should explicitly localize user-facing messages, including
validation details where applicable.

The management middleware negotiates Chinese/English using Accept-Language
quality weights. It sets Content-Language and Vary, scopes CurrentUICulture
to the request, and restores it afterwards. CurrentCulture is unchanged so
configuration parsing and numeric protocols are not affected. Proxy traffic
is not localized. Optional config.ly setting:

```yaml
Localization:
  DefaultLanguage: zh-CN
```

## Plugin fields

The SDK's `PluginConfiguration` attribute accepts `LabelKey` and
`DescriptionKey`. Schema responses retain the original `Path` and return
the resource keys plus a localized label. Common names automatically use
`fields.<Name>`. HTTP upload demonstrates plugin-specific labels. Unknown
fields retain their original display names. Sensitive values remain write-only.

Each plugin ships `Localization/zh-CN.json` and `Localization/en-US.json` as
embedded resources in its own assembly. Built-in examples are
`Plugins/Sys/HttpUpload/Localization` and `Plugins/Sys/SiteAuth/Localization`.
Standalone plugin projects add:

```xml
<ItemGroup>
  <EmbeddedResource Include="Localization/*.json" />
</ItemGroup>
```

The default resource prefix is `<plugin class namespace>.Localization`.
If the assembly's resource names use a different root namespace, set
`PluginMetadata.LocalizationResourcePrefix` to that root plus `.Localization`.
No host catalog or frontend build changes are needed to add a plugin's labels.

The config endpoint reads resources from the plugin's assembly and returns
`localization: { "zh-CN": {...}, "en-US": {...} }`. The editor registers these
under the plugin ID. Resources replace that plugin's previous catalog on load,
never the host catalog or another plugin's catalog. Resolution uses the selected
language, then the plugin's Chinese fallback, then common host labels or raw
field names. Switching language does not reload or discard an unsaved form.

Use `fields.CookieName` for conventional field labels; `LabelKey` and
`DescriptionKey` can specify plugin-owned custom keys. Treat resources as plain
display text, not HTML. `PluginLocalization.Get` is the backend lookup helper.

## Current coverage

The shared layer is connected to navigation, the language selector, login,
known toast messages, standard management errors and plugin configuration
field labels. Other business pages, plugin-supplied text and dynamic validation
messages still need explicit resource entries and calls; this is not a global
DOM or response-body string replacement. Existing untranslated text is retained.

Run frontend Node tests and `dotnet test tests/LyWaf.Tests/LyWaf.Tests.csproj`.
Catalog parity, interpolation parameters, locale fallback, request propagation,
request isolation and unchanged protocol keys have regression coverage.

## Hardcoded text audit

Run `npm run i18n:audit` from Frontend to list Chinese string literals, template
text and display attributes with file names and line numbers. The AST-based
scanner excludes comments; results are review candidates, not an automatic
replacement list. Geographic names, user content, protocol comparisons and
already-localized legacy labels must not be blindly translated.

General settings (all four tabs and dialogs), MFA, list pagination, shared
tables, feature switches, date picker controls and layout text are migrated.
Regression tests reject new Chinese display literals on those surfaces. The
two legacy runtime exit-reason comparisons remain unchanged; only their display
text is translated. Other business pages still have candidates, notably
configuration overview, plugin management, configuration editor, site management,
security events and plugin-owned pages. Plugin-owned fixes belong in that
plugin's catalogs, never in the host catalogs.

Reports, traffic metric tooltips, recent clients, geographic chart labels and
blocked-IP dialogs now use the host catalogs. The dashboard API additionally
returns numeric `uptimeSeconds`; the UI formats it in the selected language.
Map identifiers and user-entered block reasons remain unchanged. Locale changes
rebuild metric labels and chart options without waiting for a statistics refresh.
