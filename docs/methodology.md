# How Clean4ork grades work

Every restaurant on this site has a grade. This page explains, completely and
in plain English, where that grade comes from. There is no black box here: the
formula is published, the weights are published, and every restaurant page
shows the exact math behind its own score. If you want to audit a grade, the
audit is one click.

## What we read

The City of Philadelphia inspects food establishments under the FDA Food Code,
and it publishes the results — every inspection, every violation, every
correction. We read what the inspector wrote. We do not inspect restaurants
ourselves, we do not accept reports from the public, and we do not adjust
anything based on reviews, photos, or vibes. Our input is the public record,
and nothing else.

Each inspection report classifies problems into two buckets that the FDA
itself defines:

- **Foodborne Illness Risk Factors** — the dangerous ones. Improper
  temperature control, cross-contamination, sick employees handling food,
  contaminated water. These are the violations that actually make people sick.
- **Good Retail Practices** — hygiene and maintenance signals. Cleanliness,
  labeling, lighting, pest evidence in non-food areas. Worth tracking, but a
  different order of concern.

Reports also record whether each violation was **corrected on site** while the
inspector was standing there, left **out of compliance**, or flagged as a
**repeat** from a prior inspection. Those distinctions matter, and our formula
treats them very differently.

## How the score works

Every restaurant starts at 100. Deductions come off for each violation on each
inspection in the last two years:

- A Foodborne Illness Risk Factor violation costs **8 points**. A Good Retail
  Practice violation costs **2 points**.
- A violation corrected on site counts at **half weight (×0.5)** — fixing a
  problem while the inspector watches is a genuinely different signal than
  leaving it open. A violation left out of compliance counts at **×1.5**. A
  violation the inspector flagged as a repeat counts at **×2.0**.
- If the same violation code appears on more than one inspection within a
  one-year window, the later occurrence takes an additional **×1.75
  recurrence penalty**. One bad day is a bad day; the same problem twice is an
  operations problem.
- Older inspections fade. A violation carries full weight for 90 days, then
  decays linearly until, at two years old, it carries 20% of its original
  weight. Beyond two years, it drops out of the score entirely. Restaurants
  that fix their problems get to move on from them.

The result is a number from 0 to 100, bucketed into a letter: A (90+),
B (80–89), C (70–79), D (60–69), F (below 60). A restaurant with no
inspection in the last two years shows no grade at all — we don't guess.

## Where we score, and where we don't

Not every area gives us the same data. Philadelphia and some surrounding
jurisdictions publish full inspection reports — every violation, its severity,
and whether it was corrected — which is what a real grade requires. There, we
compute and show a full Clean4ork Score.

For much of the rest of Pennsylvania, the public data is thinner: a single
"passed / out of compliance" result and a date, with no violation detail. We
will not manufacture a grade from that. Those pages show the inspection result
and its date plainly, labeled as what it is — a snapshot, not a score — and we
say so on the page. A single out-of-compliance result, especially an older one,
often reflects issues noted and corrected during that same visit; it is not a
judgment about the restaurant today. As detailed reports become available in an
area, those pages graduate to a full Score.

## What a Clean4ork grade is not

A Clean4ork grade is not a government grade. Philadelphia does not issue letter
grades the way New York does; what you see here is our analysis of the city's
published inspection data, computed by the formula above. It is our opinion
about the public record, stated numerically. It is not a prediction that you
will or won't get sick, and it is not a substitute for the underlying reports,
which we link on every page.

Inspections are also snapshots. A restaurant is inspected a handful of times a
year at most, and conditions change daily. A grade summarizes the record; it
cannot summarize this afternoon.

## Claiming and the operator dashboard

Restaurant owners can claim their listing to access a dashboard showing their
score breakdown, exactly which violations are costing points, and how they
compare to peers in their category and neighborhood.

Claiming is free. The dashboard is a paid product. Neither affects the score
itself — we will not change a grade in exchange for money, and any restaurant
that thinks otherwise has misunderstood the business we are in.

## The technical appendix

For developers, journalists, regulators, and the merely curious, the entire
scoring engine is documented in our public source code at
`Clean4ork.Core/Scoring/InspectionScorer.cs`. The weights and thresholds quoted
on this page are the constants at the top of that file. Every restaurant page
on this site exposes the full list of `ScoreFactor` rows that built its score,
including the violation code, the deduction earned, the inspection date, and
whether the factor counted as a recurrence. If you want to audit a score, the
audit is one click.

We re-calibrate the bucket thresholds — the cutoffs between A, B, C, D, and
F — every quarter against the prevailing distribution of Philadelphia
restaurants. If the city's enforcement posture changes, or if the violation
mix shifts citywide, the curve shifts with it. The goal is for the grades to
remain comparable across restaurants today, not comparable to grades issued
five years ago. When we re-calibrate, we publish the change here and on our
changelog, and we never re-score historical restaurants without showing the
before-and-after.

Clean4ork is not affiliated with the City of Philadelphia, the Philadelphia
Department of Public Health, the Pennsylvania Department of Agriculture, the
FDA, or any other government body. We are a private company that reads public
records.
