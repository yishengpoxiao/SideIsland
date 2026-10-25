# QR Code Generator for .NET

The six `QrVendor*.cs` files share one implementation between the Windows app
and the portable Core project. The Windows bitmap renderer is conditional on
the target framework; QR encoding remains shared.

Upstream revision, MIT copyright notice and local adaptations are recorded in
[`THIRD-PARTY-NOTICES.txt`](../../../THIRD-PARTY-NOTICES.txt). The portable
distribution also includes `cross-platform/licenses/QR-MIT.txt`.
