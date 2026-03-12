# WTM Architecture Guard Rails

## ViewModel Rules

- All VMs extend `BaseVM` which holds `WTMContext Wtm`
- Four VM types: `BaseCRUDVM<T>`, `BasePagedListVM<T,S>`, `BaseImportVM<T>`, `BaseBatchVM<T>`
- Never bypass the VM layer to access DataContext directly from controllers

## Security

- Passwords: PBKDF2 via `PasswordHashHelper` (auto-migrates legacy MD5)
- JWT: access + refresh token rotation; `jti` claim prevents replay
- Analysis Mode: all fields whitelist-validated before entering Expression Trees

## Startup

- `services.AddWtmContext(config)` registers everything
- `app.UseWtmContext()` injects per-request context
- Framework controllers prefixed with `_` (e.g., `_AnalysisController`)

## Compatibility

- Default stance: avoid breaking changes
- Prefer additive (opt-in) over breaking
- Deprecate before removing
- Never silently change default behaviour
