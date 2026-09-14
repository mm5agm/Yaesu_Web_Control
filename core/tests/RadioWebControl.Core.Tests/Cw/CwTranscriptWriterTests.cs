using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RadioWebControl.Core.Services.Cw;
using Xunit;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// The transcript exists so that nothing decoded is lost when the operator
    /// does not press save, so these tests are almost entirely about what
    /// survives: is it on disk yet, and is it on disk before the process could
    /// have died.
    /// </summary>
    public class CwTranscriptWriterTests : IDisposable
    {
        private readonly string _dir;

        public CwTranscriptWriterTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ywc-cw-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
        }

        private static DateTime At(int h, int m, int s) =>
            new(2026, 9, 1, h, m, s, DateTimeKind.Utc);

        private CwTranscriptWriter New(int wrap = 72, Func<DateTime>? clock = null) =>
            new(_dir, At(14, 30, 0), wrap, clock ?? (() => At(14, 30, 5)));

        [Fact]
        public void A_session_that_decodes_nothing_leaves_no_file()
        {
            // Opening the reader and closing it again must leave no trace, or
            // the folder fills with empty files and the operator has to open
            // each one to find the session that had anything in it.
            using (var t = New())
            {
                t.Note("14.058 MHz CW, pitch 700 Hz");
                t.Break();
                Assert.Null(t.Path);
            }
            Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);
        }

        [Fact]
        public void The_first_decoded_character_creates_the_file()
        {
            using var t = New();
            t.Append("C");
            Assert.NotNull(t.Path);
            Assert.True(File.Exists(t.Path!));
        }

        [Fact]
        public void A_note_made_before_anything_was_decoded_still_reaches_the_file()
        {
            // Held, not dropped. The header says what the session was, and it
            // is written when the session turns out to have been one.
            using var t = New();
            t.Note("14.058 MHz CW, pitch 700 Hz");
            t.Append("CQ TEST ");
            t.Break();

            string text = ReadShared(t.Path!);
            Assert.Contains("# 14.058 MHz CW, pitch 700 Hz", text);
            Assert.Contains("CQ TEST", text);
        }

        [Fact]
        public void Decoded_text_is_on_disk_before_the_session_ends()
        {
            // This is the whole requirement. Reading the file while the writer
            // is still open is what a crash would do, so the assertion is made
            // without disposing.
            var t = New();
            t.Append("CQ CQ DE W1AW ");
            t.Break();

            Assert.Contains("CQ CQ DE W1AW", ReadShared(t.Path!));
            t.Dispose();
        }

        [Fact]
        public void The_file_is_named_for_the_session_start_not_the_first_character()
        {
            // The session started at 14:30:00 and the first character arrived
            // at 14:30:05. The name is the session.
            using var t = New();
            t.Append("E");
            Assert.Equal("cw-20260901-143000.txt", Path.GetFileName(t.Path!));
        }

        [Fact]
        public void Two_sessions_starting_in_the_same_second_do_not_collide()
        {
            using var a = New();
            a.Append("A");
            using var b = New();
            b.Append("B");

            Assert.NotEqual(a.Path, b.Path);
            Assert.Equal("cw-20260901-143000-2.txt", Path.GetFileName(b.Path!));
            Assert.Contains("A", ReadShared(a.Path!));
            Assert.Contains("B", ReadShared(b.Path!));
        }

        [Fact]
        public void Each_line_carries_the_time_it_started()
        {
            // A transcript is read by a human looking for when something was
            // heard, so the stamp is the point of the line break.
            var now = At(14, 30, 5);
            using var t = New(wrap: 0, clock: () => now);

            t.Append("FIRST ");
            t.Break();
            now = At(14, 31, 20);
            t.Append("SECOND ");
            t.Break();

            var lines = ReadLinesShared(t.Path!).Where(l => !l.StartsWith("#")).ToArray();
            Assert.Equal("[14:30:05] FIRST", lines[0]);
            Assert.Equal("[14:31:20] SECOND", lines[1]);
        }

        [Fact]
        public void Lines_wrap_at_a_word_boundary_rather_than_mid_word()
        {
            using var t = New(wrap: 30);
            t.Append("THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG AND KEEPS GOING ");
            t.Break();

            foreach (var line in ReadLinesShared(t.Path!).Where(l => !l.StartsWith("#")))
            {
                Assert.DoesNotContain("  ", line);
                // Nothing may be split: every word on the page is a whole word.
                foreach (var w in line.Split(' ').Skip(2))
                    Assert.Contains(w, "THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG AND KEEPS GOING");
            }
        }

        [Fact]
        public void A_run_with_no_spaces_in_it_is_still_written_out()
        {
            // A solid carrier decodes as an unbroken run of Es. With no word
            // boundary to wrap on, it would otherwise sit in the buffer for
            // the whole session and be lost on a crash.
            var t = New(wrap: 20);
            t.Append(new string('E', 200));

            Assert.Contains("EEEE", ReadShared(t.Path!));
            t.Dispose();
        }

        [Fact]
        public void A_line_never_starts_on_a_space()
        {
            using var t = New(wrap: 0);
            t.Append("    HELLO ");
            t.Break();

            var line = ReadLinesShared(t.Path!).First(l => !l.StartsWith("#"));
            Assert.Equal("[14:30:05] HELLO", line);
        }

        [Fact]
        public void Newlines_and_tabs_in_the_input_cannot_break_the_layout()
        {
            using var t = New(wrap: 0);
            t.Append("A\r\nB\tC");
            t.Break();

            var lines = ReadLinesShared(t.Path!).Where(l => !l.StartsWith("#")).ToArray();
            Assert.Single(lines);
            Assert.Equal("[14:30:05] A B C", lines[0]);
        }

        [Fact]
        public void Disposing_writes_out_whatever_had_not_been_broken_yet()
        {
            var t = New(wrap: 0);
            t.Append("UNFINISHED WORD");
            t.Dispose();

            Assert.Contains("UNFINISHED WORD", ReadShared(t.Path!));
        }

        [Fact]
        public void Writing_after_dispose_is_ignored_rather_than_throwing()
        {
            // The audio thread and the UI thread both reach this, and the UI
            // closing the reader must not be able to take the audio thread
            // down with it.
            var t = New();
            t.Append("HELLO ");
            t.Dispose();

            t.Append("MORE");
            t.Note("more");
            t.Break();
            Assert.DoesNotContain("MORE", ReadShared(t.Path!));
        }

        [Fact]
        public async Task Concurrent_writers_do_not_corrupt_the_file()
        {
            using var t = New(wrap: 40);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(n => Task.Run(() =>
            {
                for (int i = 0; i < 100; i++) t.Append("X");
            })));
            t.Break();

            string text = ReadShared(t.Path!);
            Assert.Equal(800, text.Count(c => c == 'X'));
            Assert.Equal(800, t.CharactersWritten);
        }

        [Fact]
        public void A_missing_directory_is_created()
        {
            Assert.False(Directory.Exists(_dir));
            using var t = New();
            t.Append("E");
            Assert.True(Directory.Exists(_dir));
        }

        /// <summary>
        /// Read a file the writer still holds open - which is both what a crash
        /// would find and what an operator gets if they open the transcript
        /// mid-session. File.ReadAllText cannot do it: its share mode does not
        /// admit the writer's own open write handle.
        /// </summary>
        private static string ReadShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }

        private static string[] ReadLinesShared(string path) =>
            ReadShared(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
