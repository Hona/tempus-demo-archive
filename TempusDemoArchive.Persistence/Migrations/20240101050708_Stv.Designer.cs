﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TempusDemoArchive.Persistence;

#nullable disable

namespace TempusDemoArchive.Persistence.Migrations
{
    [DbContext(typeof(ArchiveDbContext))]
    [Migration("20240101050708_Stv")]
    partial class Stv
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "8.0.0");

            modelBuilder.Entity("TempusDemoArchive.Persistence.Models.Demo", b =>
                {
                    b.Property<ulong>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<double>("Date")
                        .HasColumnType("REAL");

                    b.Property<bool>("StvProcessed")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Url")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("Demos");
                });

            modelBuilder.Entity("TempusDemoArchive.Persistence.Models.STVs.Stv", b =>
                {
                    b.Property<ulong>("DemoId")
                        .HasColumnType("INTEGER");

                    b.Property<double?>("IntervalPerTick")
                        .HasColumnType("REAL");

                    b.Property<int?>("StartTick")
                        .HasColumnType("INTEGER");

                    b.HasKey("DemoId");

                    b.ToTable("Stv");
                });

            modelBuilder.Entity("TempusDemoArchive.Persistence.Models.STVs.StvChat", b =>
                {
                    b.Property<ulong>("DemoId")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Index")
                        .HasColumnType("INTEGER");

                    b.Property<string>("From")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("Kind")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("Text")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int?>("Tick")
                        .HasColumnType("INTEGER");

                    b.HasKey("DemoId", "Index");

                    b.ToTable("StvChat");
                });

            modelBuilder.Entity("TempusDemoArchive.Persistence.Models.STVs.StvUser", b =>
                {
                    b.Property<ulong>("DemoId")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("UserId")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("SteamId")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("Team")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.HasKey("DemoId", "UserId");

                    b.ToTable("StvUser");
                });

            modelBuilder.Entity("TempusDemoArchive.Persistence.Models.STVs.Stv", b =>
                {
                    b.HasOne("TempusDemoArchive.Persistence.Models.Demo", "Demo")
                        .WithOne()
                        .HasForeignKey("TempusDemoArchive.Persistence.Models.STVs.Stv", "DemoId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.OwnsOne("TempusDemoArchive.Persistence.Models.STVs.StvHeader", "Header", b1 =>
                        {
                            b1.Property<ulong>("StvDemoId")
                                .HasColumnType("INTEGER");

                            b1.Property<string>("DemoType")
                                .IsRequired()
                                .HasColumnType("TEXT");

                            b1.Property<double?>("Duration")
                                .HasColumnType("REAL");

                            b1.Property<int?>("Frames")
                                .HasColumnType("INTEGER");

                            b1.Property<string>("Game")
                                .IsRequired()
                                .HasColumnType("TEXT");

                            b1.Property<string>("Map")
                                .IsRequired()
                                .HasColumnType("TEXT");

                            b1.Property<string>("Nick")
                                .IsRequired()
                                .HasColumnType("TEXT");

                            b1.Property<int?>("Protocol")
                                .HasColumnType("INTEGER");

                            b1.Property<string>("Server")
                                .IsRequired()
                                .HasColumnType("TEXT");

                            b1.Property<int?>("Signon")
                                .HasColumnType("INTEGER");

                            b1.Property<int?>("Ticks")
                                .HasColumnType("INTEGER");

                            b1.Property<int?>("Version")
                                .HasColumnType("INTEGER");

                            b1.HasKey("StvDemoId");

                            b1.ToTable("Stv");

                            b1.WithOwner()
                                .HasForeignKey("StvDemoId");
                        });

                    b.Navigation("Demo");

                    b.Navigation("Header")
                        .IsRequired();
                });

            modelBuilder.Entity("TempusDemoArchive.Persistence.Models.STVs.StvChat", b =>
                {
                    b.HasOne("TempusDemoArchive.Persistence.Models.STVs.Stv", "Stv")
                        .WithMany("Chats")
                        .HasForeignKey("DemoId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Stv");
                });

            modelBuilder.Entity("TempusDemoArchive.Persistence.Models.STVs.StvUser", b =>
                {
                    b.HasOne("TempusDemoArchive.Persistence.Models.STVs.Stv", null)
                        .WithMany("Users")
                        .HasForeignKey("DemoId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });

            modelBuilder.Entity("TempusDemoArchive.Persistence.Models.STVs.Stv", b =>
                {
                    b.Navigation("Chats");

                    b.Navigation("Users");
                });
#pragma warning restore 612, 618
        }
    }
}
