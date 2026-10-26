# Changelog

All notable changes to the SwiftGet Download Manager project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.1] - 2026-10-26

### Fixed
- **Installer (WiX MSI) ExitDialog Grey Rectangle Visual Defect**:
  - Fixed an issue on the final "Completed the SwiftGet Download Manager Setup Wizard" dialog (`ExitDialog`) where the "Launch SwiftGet Download Manager" checkbox (`OptionalCheckBox`) rendered on top of an unsightly grey rectangle (`#F0F0F0`, Win32 `COLOR_BTNFACE`) that spanned the right pane of the pure white (`#FFFFFF`, `dialog.bmp`) canvas.
  - Root cause: Windows Installer's internal Win32 `BUTTON` control implementation does not support the transparent window style (ignoring `msidbControlAttributesTransparent = 0x10000`), painting its default 220x40 dialog-unit bounding rectangle with the button-face color.
  - Applied targeted fix via `scripts/patch-msi-dialogs.ps1` integrated into `scripts/package.ps1`:
    1. Sized `OptionalCheckBox` to 10x10 dialog units, restricting the Win32 button control boundary strictly to the check glyph square with zero external padding.
    2. Retained full accessible name `"Launch SwiftGet Download Manager"` on the CheckBox control to ensure full MSAA/UIA accessibility and screen reader support.
    3. Added a companion static `Text` control (`OptionalCheckBoxText`) at `X=148, Y=190` with `Attributes=196611` (`msidbControlAttributesTransparent`) and `Control_Next=null` to preserve dialog tab ordering.
    4. Added matching show/hide `ControlCondition` for `OptionalCheckBoxText`.
  - Added automated regression test `GeneratedMsiPackage_ExitDialogOptionalCheckBox_HasVisualDefectFix` in `tests/SwiftGet.Engine.Tests/Packaging/PackagingValidationTests.cs`.
  - Captured before and after screenshots in `docs/installer-ui/before_exitdialog.png` and `docs/installer-ui/after_exitdialog.png`.
