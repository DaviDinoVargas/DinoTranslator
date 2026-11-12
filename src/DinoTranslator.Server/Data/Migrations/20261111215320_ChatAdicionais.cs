using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DinoTranslator.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatAdicionais : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PinnedAtUtc",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MessageMentions",
                columns: table => new
                {
                    MessageId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageMentions", x => new { x.MessageId, x.UserId });
                    table.ForeignKey(
                        name: "FK_MessageMentions_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MessageMentions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MessageReactions",
                columns: table => new
                {
                    MessageId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Emoji = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageReactions", x => new { x.MessageId, x.UserId, x.Emoji });
                    table.ForeignKey(
                        name: "FK_MessageReactions_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MessageReactions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ChannelId_PinnedAtUtc",
                table: "Messages",
                columns: new[] { "ChannelId", "PinnedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageMentions_UserId",
                table: "MessageMentions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageReactions_UserId",
                table: "MessageReactions",
                column: "UserId");

            // Busca por trecho no texto (ILIKE '%...%') com índice de trigramas: continua rápida
            // com muitas mensagens. Mensagens apagadas ficam fora do índice.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql("CREATE INDEX \"IX_Messages_Content_Trgm\" ON \"Messages\" USING gin (\"Content\" gin_trgm_ops) WHERE \"DeletedAtUtc\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Messages_Content_Trgm\";");

            migrationBuilder.DropTable(
                name: "MessageMentions");

            migrationBuilder.DropTable(
                name: "MessageReactions");

            migrationBuilder.DropIndex(
                name: "IX_Messages_ChannelId_PinnedAtUtc",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "PinnedAtUtc",
                table: "Messages");
        }
    }
}
