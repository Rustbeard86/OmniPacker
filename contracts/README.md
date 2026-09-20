# IPC contract fixtures

Language-neutral golden examples of the engine IPC wire format defined in
`docs/IPC-CONTRACT.md`. BOTH implementations test against these same files:

- C# engine host: `engine/tests/OmniPacker.EngineHost.Tests` (Codec fixture tests)
- Rust host: `src-tauri` `engine_ipc` module tests

Each `*.json` file is one canonical message line. Tests parse a fixture and
assert the extracted fields (structural equality), and assert that a message
they construct re-parses to the same values. We deliberately do NOT require
byte-exact re-serialization across languages (JSON key order / number formatting
differ); the guarantee is that each side can parse what the other emits and
agrees on the meaning.

When you add or change a message shape:
1. Update `docs/IPC-CONTRACT.md`.
2. Add/adjust a fixture here.
3. Add assertions on BOTH sides. A shape only counts as "in the contract" when
   both a C# test and a Rust test cover its fixture.

`evt_ready.json` uses a placeholder `engine` version ("1.2.3.4"); the live value
is the vendored engine assembly version and is asserted only for presence/shape.
