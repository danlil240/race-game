import unittest
from LongRaceScenario import TourDriver


class PursuitRouteTests(unittest.TestCase):
    def route(self, open=True):
        driver = object.__new__(TourDriver)
        driver.points = [(0, 0), (0, 100), (0, 200)]
        driver.arc = [0, 100, 200]
        driver.total = 200 if open else 400
        driver.open = open
        return driver

    def test_open_endpoint_continues_through_finish_without_wrapping(self):
        driver = self.route()
        self.assertEqual(driver._point_along(220), (0, 212))
        self.assertEqual(driver._point_along(195), (0, 195))

    def test_open_projection_ignores_fictitious_return_to_start(self):
        arc, segment = self.route()._projection({"x": 0, "z": 175})
        self.assertEqual(arc, 175)
        self.assertEqual(segment, 2)

    def test_circuit_continues_across_start(self):
        self.assertEqual(self.route(False)._point_along(425), (0, 25))


if __name__ == "__main__":
    unittest.main()
