//! Shared test helpers for AMARE crates.

/// Assert that two f64 values are within `epsilon`.
pub fn assert_close(actual: f64, expected: f64, epsilon: f64) {
    assert!(
        (actual - expected).abs() <= epsilon,
        "expected {expected} ± {epsilon}, got {actual}"
    );
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn close_values_pass() {
        assert_close(1.0, 1.0001, 0.001);
    }
}
