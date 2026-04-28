using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MySql.Data.MySqlClient;

class Program
{
    static void Main()
    {
        var listener = new TcpListener(IPAddress.Any, 5007);
        listener.Start();
        Console.WriteLine("Checkers server started on port 5007. Waiting for player to connect...");

        TcpClient tcpClient = listener.AcceptTcpClient();
        Console.WriteLine("Player connected! Starting game...\n");

        var stream = tcpClient.GetStream();
        var reader = new StreamReader(stream);
        var writer = new StreamWriter(stream) { AutoFlush = true };

        CheckersGame.Run(reader, writer);

        tcpClient.Close();
        listener.Stop();
    }
}

// ============================================================
//  CHECKERS GAME  (board shown on server; prompts/input via client)
// ============================================================
static class CheckersGame
{
    static readonly string connStr = "server=localhost;database=checkersdb;user=root;password=;";
    static char[,] board = new char[8, 8];
    static int session, moveOrder;
    static string p1Name = "", p2Name = "";
    static bool sessionClosed = false;
    static StreamReader remoteIn = null!;
    static StreamWriter remoteOut = null!;

    // Send a display-only line to the client terminal
    static void Send(string text) => remoteOut.WriteLine("PRINT:" + text);

    // Send a prompt to the client and wait for their input
    static string Ask(string prompt)
    {
        remoteOut.WriteLine("INPUT:" + prompt);
        return remoteIn.ReadLine() ?? "";
    }

    public static void Run(StreamReader reader, StreamWriter writer)
    {
        remoteIn  = reader;
        remoteOut = writer;

        SetupDatabase();

        Console.Clear();
        p1Name = Ask("Player 1 name (plays as x, starts at bottom, moves up): ").Trim();
        if (string.IsNullOrEmpty(p1Name)) p1Name = "Player1";
        p2Name = Ask("Player 2 name (plays as o, starts at top, moves down): ").Trim();
        if (string.IsNullOrEmpty(p2Name)) p2Name = "Player2";

        session       = GetNextSession();
        moveOrder     = 1;
        sessionClosed = false;
        CreateSessionRecord();

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            if (!sessionClosed) CloseSession("N/A", "abandoned");
        };
        Console.CancelKeyPress += (s, e) =>
        {
            if (!sessionClosed) CloseSession("N/A", "abandoned");
        };

        board = new char[8, 8];
        InitBoard();

        int current = 1;

        while (true)
        {
            Console.Clear();
            PrintBoard();

            string name = current == 1 ? p1Name : p2Name;
            char   sym  = current == 1 ? 'x'    : 'o';

            if (!HasAnyMoves(current))
            {
                string winner = current == 1 ? p2Name : p1Name;
                string winMsg = $"\n*** {winner} WINS! {name} has no valid moves. ***";
                Console.WriteLine(winMsg);
                Send(winMsg);
                CloseSession(winner, "completed");
                Ask("Press Enter to exit...");
                return;
            }

            Send($"\n{name}'s turn  ({sym} / {char.ToUpper(sym)} if king)");
            Send("Type 'q' at any prompt to quit.");

            var  sw    = Stopwatch.StartNew();
            bool moved = false;

            while (!moved)
            {
                var  allCaptures   = GetAllCaptures(current);
                bool forcedCapture = allCaptures.Count > 0;

                if (forcedCapture)
                    Send("\n!! A capture is available - you MUST capture !!");

                string fromIn = Ask("\nPiece to move (row col): ");
                if (fromIn == "q") { CloseSession("N/A", "abandoned"); return; }

                if (!ParseCoord(fromIn, out int fr, out int fc))
                {
                    Send("Invalid input. Use: row col  (e.g. 5 2)");
                    continue;
                }

                if (!IsPlayerPiece(board[fr, fc], current))
                {
                    Send("That is not your piece!");
                    continue;
                }

                var captures = GetCapturesForPiece(fr, fc);
                var normals  = forcedCapture
                    ? new List<(int r, int c)>()
                    : GetNormalMoves(fr, fc);

                if (forcedCapture && captures.Count == 0)
                {
                    Send("You must move a piece that CAN capture!");
                    continue;
                }

                if (captures.Count == 0 && normals.Count == 0)
                {
                    Send("That piece has no valid moves!");
                    continue;
                }

                ShowMoves(normals, captures);

                string toIn = Ask("Destination (row col): ");
                if (toIn == "q") { CloseSession("N/A", "abandoned"); return; }

                if (!ParseCoord(toIn, out int tr, out int tc))
                {
                    Send("Invalid input.");
                    continue;
                }

                var  capMatch = captures.Find(cp => cp.tr == tr && cp.tc == tc);
                bool isNormal = !forcedCapture && normals.Exists(n => n.r == tr && n.c == tc);

                if (capMatch == default && !isNormal)
                {
                    Send("That is not a valid destination!");
                    continue;
                }

                sw.Stop();
                int timeTaken = (int)sw.Elapsed.TotalSeconds;

                if (capMatch != default)
                {
                    ApplyCapture(fr, fc, tr, tc, capMatch.mr, capMatch.mc);
                    bool crowned = PromoteKing(tr, tc);
                    SaveMove(name, sym, fr, fc, tr, tc, timeTaken);

                    if (!crowned)
                    {
                        while (true)
                        {
                            Console.Clear();
                            PrintBoard();
                            var more = GetCapturesForPiece(tr, tc);
                            if (more.Count == 0) break;

                            Send($"\n{name} - you MUST continue capturing!");
                            ShowCaptures(more);

                            bool validNext = false;
                            while (!validNext)
                            {
                                string nxt = Ask("Next capture destination (row col): ");
                                if (nxt == "q") { CloseSession("N/A", "abandoned"); return; }

                                if (!ParseCoord(nxt, out int ntr, out int ntc))
                                {
                                    Send("Invalid input.");
                                    continue;
                                }

                                var nxtCap = more.Find(cp => cp.tr == ntr && cp.tc == ntc);
                                if (nxtCap == default)
                                {
                                    Send("That is not a valid capture destination!");
                                    continue;
                                }

                                ApplyCapture(tr, tc, ntr, ntc, nxtCap.mr, nxtCap.mc);
                                bool crownedMid = PromoteKing(ntr, ntc);
                                SaveMove(name, sym, tr, tc, ntr, ntc, 0);
                                tr = ntr;
                                tc = ntc;
                                validNext = true;

                                if (crownedMid) goto EndMultiJump;
                            }
                        }
                        EndMultiJump:;
                    }
                }
                else
                {
                    board[tr, tc] = board[fr, fc];
                    board[fr, fc] = '.';
                    PromoteKing(tr, tc);
                    SaveMove(name, sym, fr, fc, tr, tc, timeTaken);
                }

                moved = true;
            }

            current = current == 1 ? 2 : 1;
        }
    }

    // ---- Board initialization ----
    static void InitBoard()
    {
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                board[r, c] = '.';

        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 8; c++)
                if ((r + c) % 2 == 1) board[r, c] = 'o';

        for (int r = 5; r < 8; r++)
            for (int c = 0; c < 8; c++)
                if ((r + c) % 2 == 1) board[r, c] = 'x';
    }

    // ---- Board printing (server terminal only) ----
    static void PrintBoard()
    {
        int p1Count = 0, p2Count = 0;
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
            {
                if (IsPlayer1(board[r, c])) p1Count++;
                if (IsPlayer2(board[r, c])) p2Count++;
            }

        Console.WriteLine($"  {p1Name} (x/X): {p1Count} pieces   {p2Name} (o/O): {p2Count} pieces");
        Console.WriteLine();
        Console.WriteLine("    0 1 2 3 4 5 6 7");
        Console.WriteLine("   -----------------");

        for (int r = 0; r < 8; r++)
        {
            Console.Write($"{r} | ");
            for (int c = 0; c < 8; c++)
            {
                char cell = board[r, c];
                if      (IsPlayer1(cell)) Console.ForegroundColor = ConsoleColor.Red;
                else if (IsPlayer2(cell)) Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write(cell + " ");
                Console.ResetColor();
            }
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine("x/X = Player 1 (Red)   o/O = Player 2 (Cyan)   . = empty");
        Console.WriteLine("Uppercase = King");
    }

    // ---- Piece helpers ----
    static bool InBounds(int r, int c)            => r >= 0 && r < 8 && c >= 0 && c < 8;
    static bool IsKing(char p)                    => p == 'X' || p == 'O';
    static bool IsPlayer1(char p)                 => p == 'x' || p == 'X';
    static bool IsPlayer2(char p)                 => p == 'o' || p == 'O';
    static bool IsPlayerPiece(char p, int player) => player == 1 ? IsPlayer1(p) : IsPlayer2(p);
    static bool IsOpponent(char p, int player)    => player == 1 ? IsPlayer2(p) : IsPlayer1(p);

    static List<(int dr, int dc)> GetDirections(int r, int c)
    {
        char piece = board[r, c];
        bool king  = IsKing(piece);
        bool isP1  = IsPlayer1(piece);
        var  dirs  = new List<(int, int)>();
        if (isP1  || king) { dirs.Add((-1, -1)); dirs.Add((-1, 1)); }
        if (!isP1 || king) { dirs.Add((1,  -1)); dirs.Add((1,  1)); }
        return dirs;
    }

    // ---- Move generation ----
    static List<(int r, int c)> GetNormalMoves(int fr, int fc)
    {
        var result = new List<(int, int)>();
        foreach (var (dr, dc) in GetDirections(fr, fc))
        {
            int nr = fr + dr, nc = fc + dc;
            if (InBounds(nr, nc) && board[nr, nc] == '.')
                result.Add((nr, nc));
        }
        return result;
    }

    static List<(int tr, int tc, int mr, int mc)> GetCapturesForPiece(int fr, int fc)
    {
        var result = new List<(int, int, int, int)>();
        int player = IsPlayer1(board[fr, fc]) ? 1 : 2;
        foreach (var (dr, dc) in GetDirections(fr, fc))
        {
            int mr = fr + dr,     mc = fc + dc;
            int tr = fr + 2 * dr, tc = fc + 2 * dc;
            if (InBounds(tr, tc) && IsOpponent(board[mr, mc], player) && board[tr, tc] == '.')
                result.Add((tr, tc, mr, mc));
        }
        return result;
    }

    static List<(int tr, int tc, int mr, int mc)> GetAllCaptures(int player)
    {
        var result = new List<(int, int, int, int)>();
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                if (IsPlayerPiece(board[r, c], player))
                    result.AddRange(GetCapturesForPiece(r, c));
        return result;
    }

    static bool HasAnyMoves(int player)
    {
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                if (IsPlayerPiece(board[r, c], player))
                    if (GetCapturesForPiece(r, c).Count > 0 || GetNormalMoves(r, c).Count > 0)
                        return true;
        return false;
    }

    // ---- Move application ----
    static void ApplyCapture(int fr, int fc, int tr, int tc, int mr, int mc)
    {
        board[tr, tc] = board[fr, fc];
        board[fr, fc] = '.';
        board[mr, mc] = '.';
    }

    static bool PromoteKing(int r, int c)
    {
        if (board[r, c] == 'x' && r == 0) { board[r, c] = 'X'; return true; }
        if (board[r, c] == 'o' && r == 7) { board[r, c] = 'O'; return true; }
        return false;
    }

    // ---- UI helpers (sent to client terminal) ----
    static void ShowMoves(List<(int r, int c)> normals, List<(int tr, int tc, int mr, int mc)> captures)
    {
        var sb = new StringBuilder();
        sb.Append("Normal moves : ");
        if (normals.Count == 0) sb.Append("none");
        foreach (var (r, c) in normals) sb.Append($"({r},{c}) ");
        sb.Append("  |  Captures : ");
        if (captures.Count == 0) sb.Append("none");
        foreach (var (r, c, _, _) in captures) sb.Append($"({r},{c}) ");
        Send(sb.ToString());
    }

    static void ShowCaptures(List<(int tr, int tc, int mr, int mc)> captures)
    {
        var sb = new StringBuilder();
        sb.Append("Available captures: ");
        foreach (var (r, c, _, _) in captures) sb.Append($"({r},{c}) ");
        Send(sb.ToString());
    }

    static bool ParseCoord(string? input, out int r, out int c)
    {
        r = c = -1;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var parts = input.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out r) && int.TryParse(parts[1], out c) && InBounds(r, c);
    }

    // ---- Database ----
    static void SetupDatabase()
    {
        using var conn = new MySqlConnection(connStr);
        conn.Open();

        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS game_history (
                id         INT AUTO_INCREMENT PRIMARY KEY,
                session    INT          NOT NULL,
                player1    VARCHAR(100) NOT NULL,
                player2    VARCHAR(100) NOT NULL,
                winner     VARCHAR(100) DEFAULT NULL,
                status     VARCHAR(20)  NOT NULL DEFAULT 'in_progress',
                started_at DATETIME     DEFAULT CURRENT_TIMESTAMP,
                ended_at   DATETIME     DEFAULT NULL
            )");

        Exec(conn, "DROP TABLE IF EXISTS games");
        Exec(conn, @"
            CREATE TABLE games (
                id          INT AUTO_INCREMENT PRIMARY KEY,
                session     INT          NOT NULL,
                player_name VARCHAR(100) NOT NULL,
                symbol      CHAR(1)      NOT NULL,
                from_row    INT          NOT NULL,
                from_col    INT          NOT NULL,
                to_row      INT          NOT NULL,
                to_col      INT          NOT NULL,
                move_number INT          NOT NULL,
                time_taken  INT          NOT NULL DEFAULT 0
            )");
    }

    static int GetNextSession()
    {
        using var conn = new MySqlConnection(connStr);
        conn.Open();
        using var cmd = new MySqlCommand("SELECT COALESCE(MAX(session), 0) + 1 FROM game_history", conn);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    static void CreateSessionRecord()
    {
        using var conn = new MySqlConnection(connStr);
        conn.Open();
        using var cmd = new MySqlCommand(@"
            INSERT INTO game_history (session, player1, player2, status)
            VALUES (@s, @p1, @p2, 'in_progress')", conn);
        cmd.Parameters.AddWithValue("@s",  session);
        cmd.Parameters.AddWithValue("@p1", p1Name);
        cmd.Parameters.AddWithValue("@p2", p2Name);
        cmd.ExecuteNonQuery();
    }

    static void CloseSession(string winner, string status)
    {
        sessionClosed = true;
        using var conn = new MySqlConnection(connStr);
        conn.Open();
        using var cmd = new MySqlCommand(@"
            UPDATE game_history
            SET winner = @w, status = @st, ended_at = NOW()
            WHERE session = @s", conn);
        cmd.Parameters.AddWithValue("@w",  winner);
        cmd.Parameters.AddWithValue("@st", status);
        cmd.Parameters.AddWithValue("@s",  session);
        cmd.ExecuteNonQuery();
    }

    static void SaveMove(string playerName, char sym, int fr, int fc, int tr, int tc, int timeTaken)
    {
        using var conn = new MySqlConnection(connStr);
        conn.Open();
        using var cmd = new MySqlCommand(@"
            INSERT INTO games (session, player_name, symbol, from_row, from_col, to_row, to_col, move_number, time_taken)
            VALUES (@s, @pn, @sym, @fr, @fc, @tr, @tc, @mn, @tt)", conn);
        cmd.Parameters.AddWithValue("@s",   session);
        cmd.Parameters.AddWithValue("@pn",  playerName);
        cmd.Parameters.AddWithValue("@sym", sym.ToString());
        cmd.Parameters.AddWithValue("@fr",  fr);
        cmd.Parameters.AddWithValue("@fc",  fc);
        cmd.Parameters.AddWithValue("@tr",  tr);
        cmd.Parameters.AddWithValue("@tc",  tc);
        cmd.Parameters.AddWithValue("@mn",  moveOrder++);
        cmd.Parameters.AddWithValue("@tt",  timeTaken);
        cmd.ExecuteNonQuery();
    }

    static void Exec(MySqlConnection conn, string sql)
    {
        using var cmd = new MySqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }
}
