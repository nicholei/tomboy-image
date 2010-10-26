﻿using System;
using System.Collections.Generic;
using Mono.Unix;
using Gtk;
using System.IO;
using System.Text;
using Tomboy.InsertImage.Action;
//using AM = Mono.Addins.AddinManager;

namespace Tomboy.InsertImage
{
	public class InsertImageNoteAddin : NoteAddin
	{
		Gtk.ImageMenuItem insertLocalImageMenuItem;
		Gtk.ImageMenuItem insertWebImageMenuItem;
		List<ImageInfo> imageInfoList = new List<ImageInfo> ();

		const string SAVE_HEAD = "[Tomboy.InsertImage]";
		const string SAVE_TAIL = "[/Tomboy.InsertImage]";

		public override void Initialize ()
		{
		}

		public override void OnNoteOpened ()
		{
			insertLocalImageMenuItem = new Gtk.ImageMenuItem (
				Catalog.GetString ("Insert Local Image"));
			insertLocalImageMenuItem.Image = new Gtk.Image (Gtk.Stock.Harddisk, Gtk.IconSize.Menu);
			insertLocalImageMenuItem.Activated += OnInsertLocalImage;
			insertLocalImageMenuItem.AddAccelerator ("activate", Window.AccelGroup,
				(uint)Gdk.Key.l, Gdk.ModifierType.ControlMask | Gdk.ModifierType.ShiftMask,
				Gtk.AccelFlags.Visible);
			insertLocalImageMenuItem.Show ();
			AddPluginMenuItem (insertLocalImageMenuItem);

			insertWebImageMenuItem = new Gtk.ImageMenuItem (
				Catalog.GetString ("Insert Web Image"));
			insertWebImageMenuItem.Image = new Gtk.Image (Gtk.Stock.Network, Gtk.IconSize.Menu);
			insertWebImageMenuItem.Activated += OnInsertWebImage;
			insertWebImageMenuItem.AddAccelerator ("activate", Window.AccelGroup,
				(uint)Gdk.Key.w, Gdk.ModifierType.ControlMask | Gdk.ModifierType.ShiftMask,
				Gtk.AccelFlags.Visible);
			insertWebImageMenuItem.Show ();
			AddPluginMenuItem (insertWebImageMenuItem);

			LoadImageBoxes ();

			Note.Saved += OnNoteSaved;
			Buffer.DeleteRange += new DeleteRangeHandler (Buffer_DeleteRange);
		}

		[GLib.ConnectBefore]
		void Buffer_DeleteRange (object o, DeleteRangeArgs args)
		{
			// TODO can Tomboy allow me to access frozen_cnt?
			//if (Buffer.Undoer.frozen_cnt == 0) ...
			var iter = args.Start;
			var imagesToDel = new List<ImageInfo> ();
			while (iter.Offset < args.End.Offset) {
				var imageInfo = FindImageInfoByAnchor (iter.ChildAnchor);
				if (imageInfo != null) {
					var action = new DeleteImageAction (this, imageInfo, imageInfoList, args.Start.Offset);
					Buffer.Undoer.AddUndoAction (action);
					imagesToDel.Add (imageInfo);
				}
				if (!iter.ForwardChar ())
					break;
			}
			foreach (var info in imagesToDel) {
				info.DisplayWidth = info.Widget.ImageSize.Width;
				info.DisplayHeight = info.Widget.ImageSize.Height;
				imageInfoList.Remove (info);
			}
		}

		private ImageInfo FindImageInfoByAnchor (TextChildAnchor anchor)
		{
			if (anchor == null)
				return null;
			foreach (var info in imageInfoList) {
				if (info.Anchor == anchor)
					return info;
			}
			return null;
		}

		public override void Shutdown ()
		{
			Note.Saved -= OnNoteSaved;
			if (insertLocalImageMenuItem != null)
				insertLocalImageMenuItem.Activated -= OnInsertLocalImage;
			if (insertWebImageMenuItem != null)
				insertWebImageMenuItem.Activated -= OnInsertWebImage;
		}

		void OnNoteSaved (Note note)
		{
			if (imageInfoList.Count > 0) {
				var fileContent = File.ReadAllText (Note.FilePath);
				var sb = new StringBuilder (4096);
				int contentEndIndex = fileContent.IndexOf ("</note-content>");
				sb.Append (fileContent.Substring (0, contentEndIndex));
				sb.AppendFormat ("{0};", SAVE_HEAD);
				imageInfoList.Sort (new ImageInfoComparerByPosition ());
				foreach (var imageInfo in imageInfoList) {
					Gdk.Size displaySize = imageInfo.Widget.ImageSize;
					imageInfo.DisplayWidth = displaySize.Width;
					imageInfo.DisplayHeight = displaySize.Height;
					sb.AppendFormat ("{0}:", imageInfo.Position);
					sb.Append (imageInfo.SaveAsString());
					sb.Append (";");
				}
				sb.Append (SAVE_TAIL);
				sb.Append (fileContent.Substring (contentEndIndex));
				File.WriteAllText (Note.FilePath, sb.ToString());
			}
		}

		private void LoadImageBoxes ()
		{
			TextIter start = Buffer.StartIter;
			start.ForwardLine ();
			TextIter end = Buffer.EndIter;
			TextIter saveStart, saveEnd, tmpIter;
			bool foundSaveInfo = start.ForwardSearch(SAVE_HEAD, TextSearchFlags.TextOnly, out saveStart, out tmpIter, end);
			if (foundSaveInfo) {
				foundSaveInfo = saveStart.ForwardSearch(SAVE_TAIL, TextSearchFlags.TextOnly, out tmpIter, out saveEnd, end);
				if (foundSaveInfo) {
					Buffer.Undoer.FreezeUndo ();
					string imageElementValue = Buffer.GetSlice (saveStart, saveEnd, true);
					Buffer.Delete (ref saveStart, ref saveEnd);
					// TODO, current saveInfo reading is extremely inefficient.
					foreach (var saveInfo in imageElementValue.Split (new char [] { ';' }, StringSplitOptions.RemoveEmptyEntries)) {
						if (saveInfo.Trim () == SAVE_HEAD.Trim ())
							continue;
						if (saveInfo.Trim () == SAVE_TAIL.Trim ())
							break;
						int colonIndex = saveInfo.IndexOf (":");
						if (colonIndex == -1)
							throw new FormatException (Catalog.GetString("Invalid <image> format"));
						int offset = int.Parse (saveInfo.Substring (0, colonIndex));
						ImageInfo info = ImageInfo.FromSavedString (saveInfo.Substring(colonIndex + 1), true);
						InsertImage (Buffer.GetIterAtOffset(offset), info, false);
					}
					Buffer.Undoer.ThawUndo ();
				}
			}
		}

		void OnInsertLocalImage (object sender, EventArgs args)
		{
			InsertImage (LocalImageChooser.Instance);
		}

		void OnInsertWebImage (object sender, EventArgs args)
		{
			InsertImage (WebImageChooser.Instance);
		}

		private void InsertImage (IImageInfoChooser chooser)
		{
			ImageInfo imageInfo = null;
			try {
				imageInfo = chooser.ChooseImageInfo (Note.Window);
			} catch {
				// TODO: Report the open file error.
				imageInfo = null;
			}
			if (imageInfo == null)
				return;

			TextIter currentIter = Buffer.GetIterAtOffset (Buffer.CursorPosition);
			InsertImage (currentIter, imageInfo, true);
		}

		public void InsertImage (TextIter iter, ImageInfo imageInfo, bool supportUndo)
		{
			Gdk.Pixbuf pixbuf = null;
			try {
				pixbuf = new Gdk.Pixbuf (imageInfo.FileContent);
			} catch {
				pixbuf = null;
			}
			if (pixbuf == null) {
				// TODO: Report the open image error.
				return;
			}

			if (imageInfo.DisplayWidth == 0) {
				imageInfo.DisplayWidth = pixbuf.Width;
				imageInfo.DisplayHeight = pixbuf.Height;
			}

			var imageWidget = new ImageWidget (pixbuf);
			imageWidget.ResizeImage (imageInfo.DisplayWidth, imageInfo.DisplayHeight);
			imageWidget.ShowAll ();
			imageWidget.Resized += imageWidget_Resized;

			if (supportUndo)
				Buffer.Undoer.FreezeUndo ();
			var anchor = Buffer.CreateChildAnchor (ref iter);
			Window.Editor.AddChildAtAnchor (imageWidget, anchor);
			imageInfo.SetInBufferInfo (Buffer, anchor, imageWidget);

			//imageWidget.Destroyed += (o, e) =>
			//{
			//    if (!imageWidget.InsertUndone) {
			//        imageInfoList.Remove (imageInfo);
			//    }
			//};

			if (supportUndo) {
				Buffer.Undoer.ThawUndo ();
				var action = new InsertImageAction (this, imageInfo, imageInfoList);
				Buffer.Undoer.AddUndoAction (action);
			}
			imageInfoList.Add (imageInfo);
		}

		void imageWidget_Resized (object sender, ResizeEventArgs e)
		{
			ImageWidget widget = (ImageWidget)sender;
			ImageInfo info = imageInfoList.Find (ii => ii.Widget == widget);
			if (info != null) {
				var action = new ResizeImageAction (info, e.OldWidth, e.OldHeight, e.NewWidth, e.NewHeight);
				Buffer.Undoer.AddUndoAction (action);
			}
		}
	}
}
