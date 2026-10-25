# Shared Windows diagnostic probes

`InputDesktopProbe.cs` and `AlphaObscurationProbe.cs` are source inputs for the
Windows regression launchers. They were promoted unchanged from the original
local investigation artifacts so a fresh source checkout does not require old
logs or captured binaries.

They read the input desktop name and the owner/class/bounds of sampled windows.
They do not inject input or change Windows settings. Their **output** can contain
local process paths and desktop metadata: keep generated `artifacts/` private.
Neither file is included in the application or ordinary test executable.

`run-feature-regression.ps1` records their hashes alongside each new full run.
The original copies and complete historical evidence remain local; historical
records are not rewritten to use these new paths.
