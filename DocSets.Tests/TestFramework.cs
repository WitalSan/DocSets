using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace DocSets.Tests
{
    internal static class Assert
    {
        public static void True(bool condition, string message = null)
        {
            if (!condition) throw new TestFailureException(message ?? "Expected true, got false.");
        }

        public static void False(bool condition, string message = null) => True(!condition, message ?? "Expected false, got true.");

        public static void Null(object value, string message = null)
        {
            if (value != null) throw new TestFailureException(message ?? $"Expected null, got {value}.");
        }

        public static T NotNull<T>(T value, string message = null) where T : class
        {
            if (value == null) throw new TestFailureException(message ?? "Expected a non-null value.");
            return value;
        }

        public static void Same(object expected, object actual, string message = null)
        {
            if (!ReferenceEquals(expected, actual)) throw new TestFailureException(message ?? "Expected the same instance.");
        }

        public static void NotSame(object expected, object actual, string message = null)
        {
            if (ReferenceEquals(expected, actual)) throw new TestFailureException(message ?? "Expected different instances.");
        }

        public static void Equal<T>(T expected, T actual, string message = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new TestFailureException(message ?? $"Expected <{expected}>, got <{actual}>.");
        }

        public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message = null)
        {
            var expectedList = (expected ?? Enumerable.Empty<T>()).ToList();
            var actualList = (actual ?? Enumerable.Empty<T>()).ToList();
            if (!expectedList.SequenceEqual(actualList))
                throw new TestFailureException(message ?? $"Expected [{string.Join(", ", expectedList)}], got [{string.Join(", ", actualList)}].");
        }

        public static TException Throws<TException>(Action action) where TException : Exception
        {
            try { action(); }
            catch (TException exception) { return exception; }
            catch (Exception exception) { throw new TestFailureException($"Expected {typeof(TException).Name}, got {exception.GetType().Name}.", exception); }
            throw new TestFailureException($"Expected {typeof(TException).Name}, but no exception was thrown.");
        }
    }

    internal sealed class TestFailureException : Exception
    {
        public TestFailureException(string message, Exception inner = null) : base(message, inner) { }
    }
}
