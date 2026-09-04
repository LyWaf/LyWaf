# Support Matrix

| Component | Supported | Notes |
|---|---|---|
| .NET runtime | .NET 9 | Build and runtime baseline |
| Windows | Windows Server 2022/2025, Windows 11 x64 | CI runs on `windows-latest` |
| Linux | Current Ubuntu LTS x64 | CI runs on `ubuntu-latest` |
| Linux ARM64 | Release package generated; deployment validation required | Not yet a CI runtime target |
| Browser | Current Chrome, Edge and Firefox | Control console; JavaScript required |
| HTTP downstream | HTTP/1.1 and HTTP/2 | HTTP/3 is tracked separately |
| Stream | TCP and UDP | Platform networking behavior must be tested per deployment |
| Configuration | `config.ly`; generated YAML is internal | Validate before applying |
| Upgrade compatibility | Current release and previous minor release | Older jumps require staged upgrade and backup |
| Plugin compatibility | Exact documented host contract version | No binary compatibility promise across major contract changes |

Security fixes may narrow supported versions. Exact package versions are defined by project and frontend lock files, not this summary.
