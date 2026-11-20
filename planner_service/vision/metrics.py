"""Classification metrics shared by the training and ONNX test-set reports."""

from planner_service.vision import LABELS


def classification_metrics(matrix: list[list[int]]) -> dict:
    per_class = {}
    for index, label in enumerate(LABELS):
        true_positive = matrix[index][index]
        actual = sum(matrix[index])
        predicted = sum(row[index] for row in matrix)
        precision = true_positive / predicted if predicted else 0.0
        recall = true_positive / actual if actual else 0.0
        f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
        per_class[label] = {"precision": precision, "recall": recall, "f1": f1}
    total = sum(map(sum, matrix))
    correct = sum(matrix[i][i] for i in range(len(LABELS)))
    return {"confusion_matrix": matrix, "accuracy": correct / total if total else 0.0, "per_class": per_class}
