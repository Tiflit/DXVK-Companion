# Toy Recovery Protocol Specification

## Section 1: Protocol Messages
Synthetic message exchange rules for diagnostic agent synchronization:

| Message Kind | Required Field | Response Rule |
| :--- | :--- | :--- |
| `HeartbeatPing` | `sequence_id` (uint64) | Respond with `HeartbeatAck` within 200ms. |

*(Note: Additional message kinds pending specification.)*

## Section 2: Profile Requirements
Requirements governing the telemetry framing profile:

- **Profile A (Strict)**: Requires `payload_encoding` to be strictly UTF-8 text with mandatory newline terminators.
- **Profile B (Binary)**: Requires `payload_encoding` to be base64-wrapped binary blobs with fixed-length framing headers.

### Status: UNRESOLVED
The choice between Profile A and Profile B concerning the `payload_encoding` field is currently unresolved. Adopting Profile A prevents binary diagnostic capture, whereas adopting Profile B increases framing overhead for text-only payloads. An authorized architectural design decision is required before selecting a profile.
