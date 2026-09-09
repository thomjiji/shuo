import unittest
from shuo_asr.segmentation import AudioJob, FRAME_BYTES, PendingJobs, Segmenter, join_text

VOICE = b"\x01\x00" * (FRAME_BYTES // 2)
SILENCE = bytes(FRAME_BYTES)


class SegmentationTests(unittest.TestCase):
    def segmenter(self, **kwargs):
        return Segmenter(lambda frame: any(frame), **kwargs)

    def test_silence_and_clicks_never_reach_model(self):
        for audio in [SILENCE * 200, SILENCE * 15 + VOICE * 3 + SILENCE * 40]:
            s = self.segmenter()
            jobs = s.feed(audio) + s.finish()
            self.assertEqual([job.kind for job in jobs], ["finish"])

    def test_stop_flushes_short_voice_and_partial_frame(self):
        s = self.segmenter()
        audio = VOICE * 8 + VOICE[:122]
        jobs = []
        for start in range(0, len(audio), 1024):
            jobs.extend(s.feed(audio[start:start + 1024]))
        jobs.extend(s.finish())
        self.assertEqual([job.kind for job in jobs], ["segment", "finish"])
        self.assertEqual(jobs[0].audio[:len(audio)], audio)
        self.assertLess(len(jobs[0].audio) - len(audio), FRAME_BYTES)

    def test_pause_confirms_segment_then_next_voice_starts_new_segment(self):
        s = self.segmenter()
        jobs = s.feed(VOICE * 65 + SILENCE * 35 + VOICE * 65) + s.finish()
        finals = [j for j in jobs if j.kind == "segment"]
        self.assertEqual([j.segment for j in finals], [0, 1])
        self.assertEqual(sum(j.audio.count(VOICE) for j in finals), 130)
        self.assertTrue(any(j.kind == "preview" and j.segment == 1 for j in jobs))

    def test_continuous_audio_has_bounded_segments_without_lost_or_repeated_pcm(self):
        s = self.segmenter(max_seconds=2)
        jobs = s.feed(VOICE * 315) + s.finish()
        finals = [j for j in jobs if j.kind == "segment"]
        self.assertEqual([len(j.audio) // FRAME_BYTES for j in finals], [100, 100, 100, 15])
        self.assertEqual(b"".join(j.audio for j in finals), VOICE * 315)
        self.assertTrue(all(len(j.audio) <= 100 * FRAME_BYTES for j in jobs))

    def test_packet_boundaries_do_not_change_segmentation(self):
        audio = SILENCE * 11 + VOICE * 81 + SILENCE * 35 + VOICE * 10
        expected = self.segmenter()
        expected_jobs = expected.feed(audio) + expected.finish()
        actual = self.segmenter()
        jobs = []
        for start in range(0, len(audio), 1024):
            jobs.extend(actual.feed(audio[start:start + 1024]))
        self.assertEqual(jobs + actual.finish(), expected_jobs)

    def test_queue_coalesces_drafts_but_preserves_final_order_and_finish(self):
        q = PendingJobs()
        q.put(AudioJob("preview", 0, b"old"))
        q.put(AudioJob("preview", 0, b"new"))
        self.assertEqual(q.pop().audio, b"new")
        q.put(AudioJob("preview", 0))
        q.put(AudioJob("segment", 0))
        q.put(AudioJob("preview", 1))
        q.put(AudioJob("segment", 1))
        q.put(AudioJob("finish", 2))
        self.assertEqual([q.pop().kind for _ in range(3)], ["segment", "segment", "finish"])
        self.assertFalse(q)

    def test_overload_does_not_silently_drop_committed_audio(self):
        q = PendingJobs(max_segments=1)
        q.put(AudioJob("segment", 0, b"first"))
        with self.assertRaises(ValueError):
            q.put(AudioJob("segment", 1, b"second"))
        self.assertEqual(q.pop().audio, b"first")

    def test_join_text_keeps_chinese_adjacent_and_english_separated(self):
        self.assertEqual(join_text("第一句。", "第二句。"), "第一句。第二句。")
        self.assertEqual(join_text("First sentence.", "Second sentence."), "First sentence. Second sentence.")
        self.assertEqual(join_text("", "Text"), "Text")


if __name__ == "__main__":
    unittest.main()
