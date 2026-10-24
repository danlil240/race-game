"""Capture all four upgraded animals in both vehicles and both cameras.

Uses the existing real-player scenario and its identity assertions. Only the
expected car visual differs from the production benchmark matrix.
"""
import RacerVisualShots as roster


if __name__ == "__main__":
    roster.VEHICLES = (("car", "car-01", "kenney-racer-trial"),
                       ("moto", "motorcycle-01", "moto-01"))
    roster.main()
