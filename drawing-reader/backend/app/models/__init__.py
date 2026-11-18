"""ORM models. Importing this package registers every table on ``Base.metadata``."""

from app.db.base import Base
from app.models.auth import Permission, Role, User, role_permissions, user_roles
from app.models.bom import (
    BOMHeader,
    BOMItem,
    BOQSummary,
    CostImpact,
    MaterialRate,
    RevisionDifference,
    WeightHistory,
)
from app.models.drawing import (
    Drawing,
    DrawingFile,
    DrawingRelationship,
    DrawingRevision,
    DrawingStatus,
    DrawingTag,
    FileStatus,
)
from app.models.project import Area, Discipline, Project, System
from app.models.workflow import (
    ApprovalWorkflow,
    AuditLog,
    DocumentTransmittal,
    Notification,
    OCRJob,
    OCRJobStatus,
    OCRResult,
    ReviewComment,
    transmittal_items,
)

__all__ = [
    "ApprovalWorkflow",
    "Area",
    "AuditLog",
    "BOMHeader",
    "BOMItem",
    "BOQSummary",
    "Base",
    "CostImpact",
    "Discipline",
    "DocumentTransmittal",
    "Drawing",
    "DrawingFile",
    "DrawingRelationship",
    "DrawingRevision",
    "DrawingStatus",
    "DrawingTag",
    "FileStatus",
    "MaterialRate",
    "Notification",
    "OCRJob",
    "OCRJobStatus",
    "OCRResult",
    "Permission",
    "Project",
    "ReviewComment",
    "RevisionDifference",
    "Role",
    "System",
    "User",
    "WeightHistory",
    "role_permissions",
    "transmittal_items",
    "user_roles",
]
