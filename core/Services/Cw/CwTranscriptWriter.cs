using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RadioWebControl.Core.Services.Cw
{
    /// <summary>
    /// Writes everything the reader decodes to a timestamped text file, so that
    /// nothing is lost when the operator does not press save.
    ///
    /// That is the entire point, and it shapes three decisions that would each
    /// look wrong on their own.
    ///
    /// <b>Characters are written straight through and flushed, not buffered
    /// into lines.</b> Buffering a line before writing it would give tidier
    /// code and would lose whatever was mid-line when the process died - which
    /// is precisely the part an operator would want back, since a crash tends
    /// to happen while something is arriving rather than in the quiet after it.
    /// CW arrives at a handful of characters a second, so flushing each one
    /// costs nothing against what it protects.
    ///
    /// <b>The file is not created until something is decoded.</b> Opening the
    /// reader and closing it again leaves no trace. Otherwise the app data
    /// folder fills with empty files and the operator has to open each one to
    /// find the session that had anything in it.
    ///
    /// <b>A space is held back until the character after it arrives.</b> That
    /// is what lets a line be wrapped or ended without a trailing space, given
    /// that a character already written cannot be unwritten. It collapses runs
    /// of spaces for free.
    ///
    /// Each line is stamped with the time it began, because the file is read by
    /// a human looking for when something was heard, not parsed.
    /// </summary>
    public sealed class CwTranscriptWriter : IDisposable
    {
        private readonly object _gate = new();
        private readonly string _directory;
        private readonly DateTime _startedUtc;
        private readonly int _wrapColumns;
        private readonly Func<DateTime> _clock;
        private readonly List<string> _pendingNotes = new();

        private StreamWriter? _writer;
        private int  _column;          // 0 means no line is open
        private bool _pendingSpace;
        private bool _disposed;

        /// <summary>
        /// The file this session is writing to, or null while nothing has been
        /// decoded and so no file has been created.
        /// </summary>
        public string? Path { get; private set; }

        /// <summary>Decoded characters accepted so far.</summary>
        public long CharactersWritten { get; private set; }

        /// <param name="directory">
        /// Where transcripts live - the app data folder, alongside
        /// <c>radio_state.json</c>. Created if it does not exist.
        /// </param>
        /// <param name="startedUtc">Session start, which names the file.</param>
        /// <param name="wrapColumns">Column to wrap at; 0 or less never wraps.</param>
        /// <param name="clock">
        /// UTC clock, injectable so the tests are not at the mercy of the time
        /// of day they happen to run at.
        /// </param>
        public CwTranscriptWriter(string directory,
                                  DateTime? startedUtc = null,
                                  int wrapColumns = 72,
                                  Func<DateTime>? clock = null)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("A transcript needs a directory.", nameof(directory));

            _directory   = directory;
            _clock       = clock ?? (() => DateTime.UtcNow);
            _startedUtc  = startedUtc ?? _clock();
            _wrapColumns = wrapColumns;
        }

        /// <summary>
        /// Records what the session was - frequency, mode, pitch, whatever the
        /// caller knows - as a comment line.
        ///
        /// Held, not written, while nothing has been decoded yet, so that a
        /// header on its own never creates a file. A header describes a
        /// session; on its own it is not one.
        /// </summary>
        public void Note(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            lock (_gate)
            {
                if (_disposed) return;
                if (_writer is null) { _pendingNotes.Add(Clean(text)); return; }

                EndLine();
                _writer.WriteLine("# " + Clean(text));
                _writer.Flush();
            }
        }

        /// <summary>Appends decoded text. Safe to call one character at a time.</summary>
        public void Append(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;

            lock (_gate)
            {
                if (_disposed) return;

                foreach (char raw in text)
                {
                    // The decoder does not emit newlines, but a caller feeding
                    // this from somewhere else might, and a stray control
                    // character must not be able to fake a line break.
                    char c = raw is '\r' or '\n' or '\t' ? ' ' : raw;
                    if (char.IsControl(c)) continue;

                    CharactersWritten++;

                    if (c == ' ')
                    {
                        // A space at the head of a line is dropped rather than
                        // held: a line never starts on one.
                        if (_column > 0) _pendingSpace = true;
                        continue;
                    }

                    var w = Open();

                    if (_pendingSpace)
                    {
                        // The one place wrapping can happen: at the space, so
                        // no word is ever split and no line ends in a space.
                        if (_wrapColumns > 0 && _column >= _wrapColumns) EndLine();
                        else { w.Write(' '); _column++; }
                        _pendingSpace = false;
                    }

                    if (_column == 0) { w.Write(Stamp()); _column = StampWidth; }

                    w.Write(c);
                    _column++;

                    // A solid carrier decodes as an unbroken run with no space
                    // to wrap at. Break it anyway rather than let the line grow
                    // without limit.
                    if (_wrapColumns > 0 && _column >= _wrapColumns * 2) EndLine();

                    w.Flush();
                }
            }
        }

        /// <summary>
        /// Ends the current line, so the next thing decoded starts on its own
        /// line with its own timestamp. Called when the reader loses the signal.
        /// </summary>
        public void Break()
        {
            lock (_gate)
            {
                if (_disposed) return;
                EndLine();
                _writer?.Flush();
            }
        }

        private void EndLine()
        {
            _pendingSpace = false;
            if (_column == 0 || _writer is null) return;
            _writer.WriteLine();
            _column = 0;
        }

        private string Stamp() =>
            "[" + _clock().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] ";

        private const int StampWidth = 11;   // "[HH:mm:ss] "

        private static string Clean(string s) =>
            string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        private StreamWriter Open()
        {
            if (_writer is not null) return _writer;

            Directory.CreateDirectory(_directory);

            // Second granularity is enough - two reader sessions cannot start in
            // the same second - but a suffix costs nothing and turns a collision
            // from an exception into a second file.
            string stem = "cw-" + _startedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = System.IO.Path.Combine(_directory, stem + ".txt");
            for (int n = 2; File.Exists(path) && n < 100; n++)
                path = System.IO.Path.Combine(_directory, stem + "-" + n + ".txt");

            Path = path;
            _writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                new UTF8Encoding(false))
            {
                NewLine = "\n",
            };

            _writer.WriteLine("# CW transcript, session started "
                              + _startedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                              + " UTC. Times are UTC.");
            foreach (var note in _pendingNotes) _writer.WriteLine("# " + note);
            _pendingNotes.Clear();
            _writer.Flush();
            return _writer;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                EndLine();
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}
