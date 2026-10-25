# Shared platform-independent models

These ten C# sources are compiled by the Windows build and linked by
`cross-platform/SideIsland.Core/SideIsland.Core.csproj`. Keep their paths and
C# 5 / .NET Framework compatibility synchronized with that project.

They cover calculation, focus timing, text comparison/transformation, time and
unit conversion, file hashes, random generation and the local task activity API.
Their existing namespaces and public behavior are unchanged by the regrouping.
Windows UI, native integrations and platform adapters remain in their existing
source locations.
