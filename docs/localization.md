# Shared localization layer

The frontend and backend share `Localization/zh-CN.json` and
`Localization/en-US.json`. Keys are stable, flat identifiers; values are display
text. Add matching entries and interpolation parameters to both catalogs.

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

## Current coverage

The shared layer is connected to navigation, the language selector, login,
known toast messages, standard management errors and plugin configuration
field labels. Other business pages, plugin-supplied text and dynamic validation
messages still need explicit resource entries and calls; this is not a global
DOM or response-body string replacement. Existing untranslated text is retained.

Run frontend Node tests and `dotnet test tests/LyWaf.Tests/LyWaf.Tests.csproj`.
Catalog parity, interpolation parameters, locale fallback, request propagation,
request isolation and unchanged protocol keys have regression coverage.
