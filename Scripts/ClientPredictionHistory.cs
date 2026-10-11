using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Player.Prediction
{
    public sealed class ClientPredictionHistory
    {
        private readonly Queue<PredictionSegment> _pendingSegments = new();
        private readonly int _maxPendingSegments;

        private Vector3 _unsentDisplacement;
        private bool _hasAcknowledgedSeq;
        private uint _lastAcknowledgedSeq;
        private bool _hasAcceptedSnapshot;
        private uint _lastAcceptedSnapshotSeq;

        public int PendingCount => _pendingSegments.Count;
        public bool HistoryOverflowed { get; private set; }

        public ClientPredictionHistory(int maxPendingSegments)
        {
            if (maxPendingSegments <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPendingSegments));

            _maxPendingSegments = maxPendingSegments;
        }

        public void AccumulatePredictedDisplacement(Vector3 displacement)
        {
            displacement.y = 0f;
            _unsentDisplacement += displacement;
        }

        public void RecordSentInput(uint seqNumber)
        {
            _pendingSegments.Enqueue(new PredictionSegment(seqNumber, _unsentDisplacement));
            _unsentDisplacement = Vector3.zero;

            if (_pendingSegments.Count > _maxPendingSegments)
            {
                _pendingSegments.Dequeue();
                HistoryOverflowed = true;
            }
        }

        public bool Acknowledge(uint lastProcessedInput)
        {
            if (_hasAcknowledgedSeq && IsOlderSeq(lastProcessedInput, _lastAcknowledgedSeq))
                return false;

            _hasAcknowledgedSeq = true;
            _lastAcknowledgedSeq = lastProcessedInput;

            while (_pendingSegments.Count > 0 &&
                   !IsOlderSeq(lastProcessedInput, _pendingSegments.Peek().SeqNumber))
            {
                _pendingSegments.Dequeue();
            }

            return true;
        }

        public bool TryAcceptSnapshot(uint snapshotSeq)
        {
            if (_hasAcceptedSnapshot && !IsNewerSeq(snapshotSeq, _lastAcceptedSnapshotSeq))
                return false;

            _hasAcceptedSnapshot = true;
            _lastAcceptedSnapshotSeq = snapshotSeq;
            return true;
        }

        public Vector3 GetReplayOffset()
        {
            Vector3 offset = _unsentDisplacement;

            foreach (var segment in _pendingSegments)
            {
                offset += segment.PredictedDisplacement;
            }

            offset.y = 0f;
            return offset;
        }

        public void Clear()
        {
            _pendingSegments.Clear();
            _unsentDisplacement = Vector3.zero;
            HistoryOverflowed = false;
            _hasAcknowledgedSeq = false;
            _lastAcknowledgedSeq = 0;
            _hasAcceptedSnapshot = false;
            _lastAcceptedSnapshotSeq = 0;
        }

        private static bool IsOlderSeq(uint candidate, uint reference)
        {
            return unchecked((int)(candidate - reference)) < 0;
        }

        private static bool IsNewerSeq(uint candidate, uint reference)
        {
            return unchecked((int)(candidate - reference)) > 0;
        }

        private readonly struct PredictionSegment
        {
            public uint SeqNumber { get; }
            public Vector3 PredictedDisplacement { get; }

            public PredictionSegment(uint seqNumber, Vector3 predictedDisplacement)
            {
                SeqNumber = seqNumber;
                PredictedDisplacement = predictedDisplacement;
            }
        }
    }
}
