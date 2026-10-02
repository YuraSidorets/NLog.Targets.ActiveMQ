# Changelog
All notable changes to this project will be documented in this file.

## [2.1.3] – Unreleased
### Fixed
- Disposed partially initialized ActiveMQ resources and made shutdown cleanup fault-tolerant.
- Synchronized direct target disposal with NLog's write/close lock.
- Removed connection URI and destination values from target-generated diagnostic messages.
- Corrected the build workflow push trigger and kept package publishing tag-only.

### Changed
- Added NuGet license and project URL metadata.
- Updated Apache.NMS.ActiveMQ to 2.2.0 while retaining the NLog 5.2.2 minimum and netstandard2.0 compatibility.
- Enabled nullable analysis, .NET analyzers, and warnings-as-errors for the library.
- Updated test tooling and CI to .NET 10.
- Replaced the custom lazy container helper with xUnit lifecycle management and random port allocation.
- Expanded lifecycle, failure-path, configuration, and real-broker test coverage.
- Added release tag/version validation and full tests before NuGet publishing.
- Documented bounded asynchronous queuing and NMS failover configuration.

## [2.1.2] – 2025-04-24
### Added
- Added Source Link support and deterministic CI builds with symbol packages.

## [2.1.1] – 2025-04-24
### Added
- Included the README in the NuGet package.

### Fixed
- Upgraded Apache.NMS.ActiveMQ to 2.1.1 to address CVE-2025-29953.
