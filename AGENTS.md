# Briefcase Sync

Read README.md, docs/architecture.md, docs/mapping.md and docs/operations.md before edits.
For UI changes, also read docs/ui.md and preserve the pinned design-system reference CSS byte-for-byte.
Use the sibling MassTortArchitecture standards and mt-software-catalog for architecture/service choices.

- This is a standalone .NET app. Do not add CaseConnector or CaseWorks dependencies.
- Keep direct vendor transport in Infrastructure and business decisions in Core.
- Tiger Lead ID is leadVendorId; never generate a fallback or overwrite SmartAdvocate case numbers.
- Freeze delivery inputs before HTTP. Intake retries use the same ID and request; uncertain upload initialization requires explicit reconciliation.
- SQL session locks must remain on the same connection as state commands. Disable transparent reconnect.
- Publish the database with its SQL project. Runtime code has no schema migrations or DDL grants.
- Keep sensitive values, payloads, filenames, signed URLs and raw exceptions out of telemetry and examples.
- Use synthetic fixtures. Run eng/verify.ps1; use the opt-in local SQL integration test when a test server is available.
- Default configuration is unapproved/sending-disabled. Live vendor writes and Azure deployment need explicit session authorization.
