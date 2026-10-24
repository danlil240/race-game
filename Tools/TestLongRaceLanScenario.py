import copy
import unittest

from LongRaceLanScenario import validate_results


class LanResultContractTests(unittest.TestCase):
    def race(self):
        return {
            "phase": "finished",
            "racers": [{"session": {"courseId": "journey"}} for _ in range(2)],
            "result": {"formatVersion": 1, "courseId": "", "eventId": "event", "resultId": "race/result",
                       "racers": [{"slot": slot, "attribution": "human", "outcome": "finished", "finishTicks": 9000 + slot}
                                  for slot in range(2)]},
        }

    def test_original_lan_result_uses_session_course_identity(self):
        host = self.race()
        result, humans = validate_results(host, copy.deepcopy(host), "event", "journey")
        self.assertEqual(result["courseId"], "")
        self.assertEqual(len(humans), 2)

    def test_wrong_guest_course_is_rejected(self):
        host, guest = self.race(), self.race()
        guest["racers"][0]["session"]["courseId"] = "old-circuit"
        with self.assertRaises(AssertionError):
            validate_results(host, guest, "event", "journey")

    def test_divergent_finish_is_rejected(self):
        host, guest = self.race(), self.race()
        guest["result"]["racers"][0]["finishTicks"] += 1
        with self.assertRaises(AssertionError):
            validate_results(host, guest, "event", "journey")


if __name__ == "__main__":
    unittest.main()
