package me.vbu.mirrordirectories.ui.views.components;

import javafx.scene.control.ScrollPane;
import javafx.scene.control.TreeItem;
import javafx.scene.control.TreeView;
import javafx.scene.layout.Priority;
import javafx.scene.layout.VBox;
import me.vbu.mirrordirectories.model.filesystem.DirectoryNode;
import me.vbu.mirrordirectories.model.filesystem.Node;

/**
 * Component for displaying the directory comparison tree.
 */
public class DirectoryTreeView extends VBox {

    private TreeView<String> treeView;

    public DirectoryTreeView() {
        initializeUI();
    }

    private void initializeUI() {
        // Create directory diff tree view
        treeView = new TreeView<>();
        treeView.setShowRoot(true);
        treeView.setRoot(new TreeItem<>("Directory Differences"));

        // Create tree container with scroll pane
        ScrollPane scrollPane = new ScrollPane(treeView);
        scrollPane.setFitToWidth(true);
        scrollPane.setFitToHeight(true);

        this.getChildren().add(scrollPane);
        VBox.setVgrow(scrollPane, Priority.ALWAYS);
        this.setPadding(new javafx.geometry.Insets(10, 0, 10, 0));
    }

    /**
     * Updates the tree view with comparison results.
     *
     * @param rootNode The root node of the comparison result
     */
    public void updateTreeView(DirectoryNode rootNode) {
        // Clear existing tree
        TreeItem<String> root = new TreeItem<>(rootNode.getName() + " (Root)");
        root.setExpanded(true);

        // Build tree items from comparison results
        if (rootNode.hasChildren()) {
            for (Node child : rootNode.getChildren().values()) {
                addNodeToTree(child, root);
            }
        }

        treeView.setRoot(root);
    }

    /**
     * Recursively adds nodes to the tree view.
     *
     * @param node The node to add
     * @param parent The parent tree item
     */
    private void addNodeToTree(Node node, TreeItem<String> parent) {
        // Create tree item for this node
        String displayName = node.getName() + (node.isDirectory() ? "/" : "");
        TreeItem<String> item = new TreeItem<>(displayName);

        // Add children if this is a directory
        if (node.isDirectory()) {
            item.setExpanded(true);
            DirectoryNode dirNode = (DirectoryNode) node;
            for (Node child : dirNode.getChildren().values()) {
                addNodeToTree(child, item);
            }
        }

        // Add to parent
        parent.getChildren().add(item);
    }
}
