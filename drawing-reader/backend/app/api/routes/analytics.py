from __future__ import annotations

from typing import Annotated

from fastapi import APIRouter, Query
from fastapi.responses import Response, StreamingResponse

from app.api.deps import CurrentUser, DbSession
from app.schemas.analytics import (
    AIQueryRequest,
    AIQueryResponse,
    CostImpactSummaryRow,
    DashboardCharts,
    DashboardSummary,
    MaterialAnalysisRow,
    WeightGrowthSeries,
)
from app.schemas.common import ERROR_RESPONSES, ApiResponse
from app.services import ai_assistant, analytics, reports
from app.services.cache import cached

dashboard = APIRouter(prefix="/dashboard", tags=["Dashboard"], responses=ERROR_RESPONSES)
analytics_router = APIRouter(prefix="/analytics", tags=["Analytics"], responses=ERROR_RESPONSES)
ai = APIRouter(prefix="/ai", tags=["AI Assistant"], responses=ERROR_RESPONSES)
reports_router = APIRouter(prefix="/reports", tags=["Reports"], responses=ERROR_RESPONSES)


@dashboard.get("/summary", response_model=ApiResponse[DashboardSummary], summary="KPI cards")
def summary(db: DbSession, _: CurrentUser, project_id: int | None = None) -> ApiResponse[DashboardSummary]:
    data = cached(f"dash:summary:{project_id}", lambda: analytics.dashboard_summary(db, project_id))
    return ApiResponse(data=DashboardSummary.model_validate(data))


@dashboard.get("/charts", response_model=ApiResponse[DashboardCharts], summary="Dashboard chart series")
def charts(db: DbSession, _: CurrentUser, project_id: int | None = None) -> ApiResponse[DashboardCharts]:
    data = cached(f"dash:charts:{project_id}", lambda: analytics.dashboard_charts(db, project_id))
    return ApiResponse(data=DashboardCharts.model_validate(data))


@analytics_router.get(
    "/material-analysis",
    response_model=ApiResponse[list[MaterialAnalysisRow]],
    summary="Material share of total weight with estimated cost",
)
def material_analysis(
    db: DbSession, _: CurrentUser, project_id: int | None = None
) -> ApiResponse[list[MaterialAnalysisRow]]:
    data = cached(f"an:material:{project_id}", lambda: analytics.material_analysis(db, project_id))
    return ApiResponse(data=[MaterialAnalysisRow.model_validate(r) for r in data])


@analytics_router.get(
    "/weight-growth",
    response_model=ApiResponse[list[WeightGrowthSeries]],
    summary="Monthly weight growth (delta and cumulative) per project or discipline",
)
def weight_growth(
    db: DbSession,
    _: CurrentUser,
    project_id: int | None = None,
    group_by: Annotated[str, Query(pattern="^(project|discipline)$")] = "project",
) -> ApiResponse[list[WeightGrowthSeries]]:
    data = cached(f"an:growth:{project_id}:{group_by}", lambda: analytics.weight_growth(db, project_id, group_by))
    return ApiResponse(data=[WeightGrowthSeries.model_validate(s) for s in data])


@analytics_router.get(
    "/cost-impact",
    response_model=ApiResponse[list[CostImpactSummaryRow]],
    summary="Revision changes ranked by absolute cost impact",
)
def cost_impact(
    db: DbSession, _: CurrentUser, project_id: int | None = None, limit: Annotated[int, Query(ge=1, le=1000)] = 50
) -> ApiResponse[list[CostImpactSummaryRow]]:
    return ApiResponse(
        data=[CostImpactSummaryRow.model_validate(r) for r in analytics.cost_impact_summary(db, project_id, limit)]
    )


@analytics_router.get(
    "/procurement-impact",
    response_model=ApiResponse[list[dict]],
    summary="Cost/weight impact of revisions per material",
)
def procurement(db: DbSession, _: CurrentUser, project_id: int | None = None) -> ApiResponse[list[dict]]:
    return ApiResponse(data=analytics.procurement_impact(db, project_id))


@ai.post(
    "/query",
    response_model=ApiResponse[AIQueryResponse],
    summary="Ask an engineering question; answered with SQL over the register",
)
def ai_query(body: AIQueryRequest, db: DbSession, _: CurrentUser) -> ApiResponse[AIQueryResponse]:
    return ApiResponse(data=AIQueryResponse.model_validate(ai_assistant.answer(db, body.question, body.project_id)))


@reports_router.get(
    "/{report_type}",
    summary="Generate and download a report",
    description="report_type: " + ", ".join(reports.REPORT_TYPES) + ". format: csv | xlsx | pdf.",
    responses={200: {"content": {m: {} for m in reports.FORMATS.values()}}},
)
def download_report(
    report_type: str,
    db: DbSession,
    _: CurrentUser,
    format: Annotated[str, Query(pattern="^(csv|xlsx|pdf)$")] = "xlsx",  # noqa: A002
    project_id: int | None = None,
    drawing_id: int | None = None,
    from_revision: str | None = None,
    to_revision: str | None = None,
) -> Response:
    report = reports.build(
        db,
        report_type,
        {
            "project_id": project_id,
            "drawing_id": drawing_id,
            "from_revision": from_revision,
            "to_revision": to_revision,
        },
    )
    filename = f"{report_type}.{format}"
    headers = {"Content-Disposition": f'attachment; filename="{filename}"'}
    if format == "csv":
        return StreamingResponse(reports.to_csv(report), media_type=reports.FORMATS["csv"], headers=headers)
    body = reports.to_xlsx(report) if format == "xlsx" else reports.to_pdf(report)
    return Response(body, media_type=reports.FORMATS[format], headers=headers)
