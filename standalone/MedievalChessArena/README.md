# Medieval Chess Arena

A standalone C# WPF 3D chess board with one authoritative rules engine and local connections for a human, Codex, and Ali.

## Play

- Click a piece and then a legal destination, or enter a UCI move such as `e2e4`.
- Assign Human, Codex, or Ali to either side with the commander selectors.
- The board enforces check, checkmate, stalemate, castling, en passant, and promotion.
- Use **Turn board** to change perspective and **Export PGN** to save the battle.

## Local agent connections

The arena only listens on the loopback interface. It does not expose the game to the network.

- REST state: `http://127.0.0.1:39464/api/state`
- REST move: `POST http://127.0.0.1:39464/api/move` with `{ "actor": "Codex", "move": "e2e4" }`
- REST side claim: `POST http://127.0.0.1:39464/api/claim` with `{ "actor": "Ali", "side": "Black" }`
- Codex MCP war gate: `http://127.0.0.1:39464/mcp`
- Ali MCP war gate: `http://127.0.0.1:39465/mcp`

MCP tools:

- `chess_get_state`
- `chess_claim_side`
- `chess_make_move`
- `chess_reset_game`
- `chess_export_pgn`

Each MCP gate binds its tools to its named combatant, so neither fighter can impersonate the other. Both gates and the spectator API operate on the exact same in-memory game displayed by WPF.

## Build and verify

```powershell
dotnet build .\MedievalChessArena.csproj --configuration Release
dotnet run --project ..\MedievalChessArena.SmokeTests\MedievalChessArena.SmokeTests.csproj --configuration Release
```
