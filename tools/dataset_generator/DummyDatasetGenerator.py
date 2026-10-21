import numpy as np
import pandas as pd
from sklearn.datasets import make_blobs, make_swiss_roll
import os


# =====================================================
# GENERATORS
# =====================================================

def pure_noise(samples=10000, dimensions=100):
    return np.random.uniform(-1, 1, (samples, dimensions))


def gaussian_cloud(samples=10000, dimensions=100):
    return np.random.normal(0, 1, (samples, dimensions))


def hidden_gaussian(samples=10000,
                    dimensions=100,
                    latent_dims=3,
                    signal_strength=10):

    noise_dims = dimensions - latent_dims

    signal = np.random.normal(
        0,
        signal_strength,
        (samples, latent_dims)
    )

    noise = np.random.uniform(
        -1,
        1,
        (samples, noise_dims)
    )

    return np.hstack([signal, noise])


def clustered(samples=10000,
              dimensions=100,
              clusters=5,
              latent_dims=3,
              cluster_std=1.0):

    latent, labels = make_blobs(
        n_samples=samples,
        centers=clusters,
        n_features=latent_dims,
        cluster_std=cluster_std,
        random_state=42
    )

    noise_dims = dimensions - latent_dims
    noise = np.random.uniform(-1, 1, (samples, noise_dims))

    X = np.hstack([latent, noise])

    return X, labels


def correlated_gaussian(samples=10000,
                        dimensions=100):

    cov = np.array([
        [1.0, 0.95, 0.80],
        [0.95, 1.0, 0.85],
        [0.80, 0.85, 1.0]
    ])

    latent = np.random.multivariate_normal(
        [0, 0, 0],
        cov,
        samples
    )

    noise = np.random.uniform(
        -1,
        1,
        (samples, dimensions - 3)
    )

    return np.hstack([latent, noise])


def hollow_sphere(samples=10000,
                  dimensions=100):

    phi = np.random.uniform(0, np.pi, samples)
    theta = np.random.uniform(0, 2 * np.pi, samples)

    x = np.sin(phi) * np.cos(theta)
    y = np.sin(phi) * np.sin(theta)
    z = np.cos(phi)

    sphere = np.column_stack([x, y, z])

    noise = np.random.normal(
        0,
        0.05,
        (samples, dimensions - 3)
    )

    return np.hstack([sphere, noise])


def nested_spheres(samples=10000,
                   dimensions=100):

    radii = [1, 3, 6]

    all_points = []
    labels = []

    base_shell = samples // len(radii)
    remainder = samples % len(radii)

    for i, r in enumerate(radii):

        per_shell = base_shell + (1 if i < remainder else 0)

        phi = np.random.uniform(0, np.pi, per_shell)
        theta = np.random.uniform(0, 2*np.pi, per_shell)

        x = r * np.sin(phi) * np.cos(theta)
        y = r * np.sin(phi) * np.sin(theta)
        z = r * np.cos(phi)

        pts = np.column_stack([x, y, z])

        noise = np.random.normal(
            0,
            0.05,
            (per_shell, dimensions - 3)
        )

        all_points.append(np.hstack([pts, noise]))
        labels.extend([i] * per_shell)

    return np.vstack(all_points), np.array(labels)


def swiss_roll(samples=10000,
               dimensions=100):

    latent, labels = make_swiss_roll(
        n_samples=samples,
        noise=0.1,
        random_state=42
    )

    noise = np.random.uniform(
        -1,
        1,
        (samples, dimensions - 3)
    )

    X = np.hstack([latent, noise])

    return X, labels


def moving_clusters(samples_per_step=2000,
                     steps=20,
                     dimensions=100,
                     clusters=5,
                     latent_dims=3,
                     cluster_std=1.0,
                     drift_speed=0.3,
                     seed=42):
    """
    Cluster centers drift linearly over a series of discrete time steps.
    Every row carries a Time value (0..steps-1) alongside its Label,
    so a single CSV encodes a full scrubbable timelapse: filtering rows
    to a given Time value reproduces a normal static 'clustered()' style
    snapshot at that point in the animation.

    Returns (X, labels, times) - same shape convention as the other
    generators, with an extra 'times' array to export as a Time column.
    """

    rng = np.random.default_rng(seed)

    # Fixed starting centers + a fixed per-cluster drift direction/speed.
    # Centers at step t = start_centers + drift * t, so motion is linear
    # and fully deterministic given the seed (reproducible timelapse).
    start_centers = rng.normal(0, 5, (clusters, latent_dims))
    drift = rng.normal(0, drift_speed, (clusters, latent_dims))

    samples_per_cluster = samples_per_step // clusters
    remainder = samples_per_step % clusters

    all_rows = []
    all_labels = []
    all_times = []

    for t in range(steps):
        step_centers = start_centers + drift * t

        for c in range(clusters):
            count = samples_per_cluster + (1 if c < remainder else 0)

            latent = rng.normal(
                step_centers[c],
                cluster_std,
                (count, latent_dims)
            )

            all_rows.append(latent)
            all_labels.extend([c] * count)
            all_times.extend([t] * count)

    latent_all = np.vstack(all_rows)

    noise_dims = dimensions - latent_dims
    noise = rng.uniform(-1, 1, (latent_all.shape[0], noise_dims))

    X = np.hstack([latent_all, noise])

    return X, np.array(all_labels), np.array(all_times)


# =====================================================
# EXPORT
# =====================================================

def export_csv(data, filename, labels=None, extra_columns=None):
    """
    extra_columns: optional dict of {column_name: array}, appended after
    Dim_0..Dim_N and after Label (if provided). Lets datasets carry
    additional metadata columns like Time without every generator/call
    site needing to know about each other.
    """

    df = pd.DataFrame(
        data,
        columns=[f"Dim_{i}" for i in range(data.shape[1])]
    )

    if labels is not None:
        df["Label"] = labels

    if extra_columns:
        for column_name, values in extra_columns.items():
            df[column_name] = values

    df.to_csv(filename, index=False)

    print(f"Saved: {filename}")
    print(f"Shape : {df.shape}")


# =====================================================
# BENCHMARK POINT-COUNT TIERS
# =====================================================

def generate_benchmark_tier(point_count,
                             dimensions=8,
                             latent_dims=3,
                             clusters=5,
                             cluster_std=1.0):
    """
    Generates one point-count tier for the rendering pipeline benchmark,
    using clustered() so the resulting scatterplot has real visible
    structure (useful for eyeballing "does this pipeline still look
    correct at 10M points", not just for timing numbers).

    Deliberately uses far fewer dimensions than the other generators in
    this file (default 8, not 100) - the benchmark's independent variable
    is POINT COUNT, and the Unity visualizer only ever reads 3-4 columns
    (X/Y/Z/color) regardless of how many total columns exist. Carrying 100
    columns at 10^7 rows would mean ~12-15GB CSV files for no benchmarking
    benefit; 8 columns keeps the largest tier around ~1-1.5GB instead.
    """
    return clustered(
        samples=point_count,
        dimensions=dimensions,
        clusters=clusters,
        latent_dims=latent_dims,
        cluster_std=cluster_std
    )


def export_benchmark_tiers(point_counts=(10_000, 100_000, 1_000_000, 10_000_000),
                            dimensions=8,
                            output_dir="benchmark_tiers"):
    """
    Exports one CSV per point-count tier for the rendering pipeline
    benchmark. Filenames encode the exact row count so it's unambiguous
    which tier is which once they're sitting in StreamingAssets later.

    NOTE: this only produces the raw CSVs. Each one still needs to be run
    through preprocess_csv_columnar.py separately to produce the actual
    .cdataset files the Unity harness (BenchmarkingManager) reads from -
    that's a distinct manual step, not something this script does.

    Heads up on the largest tier: 10^7 rows is a genuinely large
    generation/export job (expect it to take noticeably longer than the
    other datasets in this file, and to produce a multi-hundred-MB to
    ~1GB+ CSV depending on `dimensions`). Drop the largest tier from
    `point_counts` first if you just want to validate the pipeline works
    end to end before committing to the full run.
    """
    os.makedirs(output_dir, exist_ok=True)

    for count in point_counts:
        X, y = generate_benchmark_tier(count, dimensions=dimensions)

        # Human-readable tier label in the filename (e.g. 1e4, 1e5, 1e6, 1e7)
        # rather than the literal row count, so filenames stay short and
        # the tier is instantly recognizable.
        exponent = len(str(count)) - 1
        label = f"1e{exponent}"

        export_csv(
            X,
            os.path.join(output_dir, f"benchmark_{label}.csv"),
            labels=y
        )




if __name__ == "__main__":

    os.makedirs("synthetic_datasets", exist_ok=True)

    export_csv(
        pure_noise(),
        "synthetic_datasets/pure_noise.csv"
    )

    export_csv(
        gaussian_cloud(),
        "synthetic_datasets/gaussian_cloud.csv"
    )

    export_csv(
        hidden_gaussian(signal_strength=10),
        "synthetic_datasets/hidden_gaussian.csv"
    )

    X, y = clustered()
    export_csv(
        X,
        "synthetic_datasets/clusters.csv",
        y
    )

    export_csv(
        correlated_gaussian(),
        "synthetic_datasets/correlated.csv"
    )

    export_csv(
        hollow_sphere(),
        "synthetic_datasets/hollow_sphere.csv"
    )

    X, y = nested_spheres()
    export_csv(
        X,
        "synthetic_datasets/nested_spheres.csv",
        y
    )

    X, y = swiss_roll()
    export_csv(
        X,
        "synthetic_datasets/swiss_roll.csv",
        y
    )

    X, y, t = moving_clusters()
    export_csv(
        X,
        "synthetic_datasets/moving_clusters.csv",
        labels=y,
        extra_columns={"Time": t}
    )

    print("\nGenerated all datasets.")

    # Benchmark point-count tiers - separate output folder, separate concern
    # from the exploratory datasets above. Comment out the 10^7 tier below
    # if you just want to validate the harness end to end first.
    export_benchmark_tiers(
        point_counts=(10_000, 100_000, 1_000_000, 10_000_000)
    )

    print("\nGenerated benchmark point-count tiers.")