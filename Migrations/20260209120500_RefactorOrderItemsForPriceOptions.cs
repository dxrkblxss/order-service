using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderService.Migrations
{
    public partial class RefactorOrderItemsForPriceOptions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "price_option_id",
                table: "order_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "unit_price_at_order_time",
                table: "order_items",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "unit_label",
                table: "order_items",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "order_histories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    changed_by = table.Column<string>(type: "text", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_histories", x => x.id);
                    table.ForeignKey(
                        name: "fk_order_histories_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_histories_order_id",
                table: "order_histories",
                column: "order_id");

            migrationBuilder.DropColumn(name: "price_at_time_of_order", table: "order_items");
            migrationBuilder.DropColumn(name: "price_unit", table: "order_items");
            migrationBuilder.DropColumn(name: "weight", table: "order_items");
            migrationBuilder.DropColumn(name: "unit", table: "order_items");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "price_at_time_of_order",
                table: "order_items",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "price_unit",
                table: "order_items",
                type: "text",
                nullable: false,
                defaultValue: "шт");

            migrationBuilder.AddColumn<decimal>(
                name: "weight",
                table: "order_items",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "unit",
                table: "order_items",
                type: "text",
                nullable: false,
                defaultValue: "г");

            migrationBuilder.DropTable(name: "order_histories");

            migrationBuilder.DropColumn(name: "price_option_id", table: "order_items");
            migrationBuilder.DropColumn(name: "unit_price_at_order_time", table: "order_items");
            migrationBuilder.DropColumn(name: "unit_label", table: "order_items");
        }
    }
}
